using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Stadion.SmartDrift.PriceMonitor.Core;

namespace Stadion.SmartDrift.PriceMonitor.Crawler;

public sealed record PlatouSportOptions(int MaxSitemaps = 30, int MaxProductPages = 500, int MaxResponseBytes = 2_000_000,
    TimeSpan? RequestInterval = null);

public sealed class PlatouSportCrawler(HttpClient client, PlatouSportOptions? options = null) : IWebsiteCrawler
{
    private const string Agent = "StadionSmartDriftPriceMonitor";
    private static readonly Uri Origin = new("https://www.platousport.com/");
    private static readonly XNamespace SitemapNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";
    private readonly PlatouSportOptions settings = options ?? new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset? lastRequest;
    public string Key => "platou-sport";

    public async Task<CrawlResult> CrawlAsync(CrawlTarget target, CancellationToken cancellationToken)
    {
        if (settings.MaxSitemaps <= 0 || settings.MaxProductPages <= 0 || settings.MaxResponseBytes <= 0 ||
            settings.RequestInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));

        await gate.WaitAsync(cancellationToken);
        try
        {
            var interval = settings.RequestInterval ?? TimeSpan.FromSeconds(1);
            async Task<string> Fetch(Uri url)
            {
                if (lastRequest is not null)
                {
                    var remaining = interval - (DateTimeOffset.UtcNow - lastRequest.Value);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
                }
                lastRequest = DateTimeOffset.UtcNow;
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd($"{Agent}/1.0");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > settings.MaxResponseBytes)
                    throw new InvalidDataException("Response exceeds the size limit.");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream();
                var bytes = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(bytes, cancellationToken)) > 0)
                {
                    if (buffer.Length + count > settings.MaxResponseBytes)
                        throw new InvalidDataException("Response exceeds the size limit.");
                    buffer.Write(bytes, 0, count);
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }

            var robots = RobotsRules.Parse(await Fetch(new Uri(Origin, "robots.txt")));
            if (robots.Delay > interval) interval = robots.Delay;
            var index = new Uri(Origin, "sitemap_index.xml");
            if (!robots.Allows(index)) return Result(CrawlStatus.Failed, "Sitemap index disallowed by robots.txt.");
            var sitemaps = ParseSitemap(await Fetch(index), "sitemapindex", "sitemap")
                .Where(IsProductSitemap).Distinct().ToList();
            if (sitemaps.Count == 0) return Result(CrawlStatus.Failed, "No product sitemaps found.");

            CrawlResult? fallback = null;
            var pages = new HashSet<Uri>();
            var truncated = sitemaps.Count > settings.MaxSitemaps;
            foreach (var sitemap in sitemaps.Take(settings.MaxSitemaps))
            {
                if (!robots.Allows(sitemap)) return Result(CrawlStatus.Failed, "Product sitemap disallowed by robots.txt.");
                var urls = ParseSitemap(await Fetch(sitemap), "urlset", "url").Where(IsProductPage);
                foreach (var url in urls)
                {
                    if (pages.Count >= 100_000) { truncated = true; break; }
                    pages.Add(url);
                }
                if (pages.Count >= 100_000) break;
            }
            var terms = Regex.Matches(target.ProductName ?? "", @"[a-zA-Z0-9]{3,}")
                .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var ordered = pages.OrderByDescending(url => terms.Count(term =>
                Uri.UnescapeDataString(url.AbsolutePath).Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
            truncated |= ordered.Length > settings.MaxProductPages;
            foreach (var url in ordered.Take(settings.MaxProductPages))
            {
                if (!robots.Allows(url)) { truncated = true; continue; }
                var result = PlatouSportProductParser.Parse(await Fetch(url), url, target, DateTimeOffset.UtcNow);
                if (result.ProductMatches.FirstOrDefault()?.MatchBasis == "gtin") return result;
                if (result.Status == CrawlStatus.Incomplete) truncated = true;
                if (result.ProductMatches.Count > 0 && (fallback is null ||
                    result.ProductMatches[0].MatchBasis == "article-number"))
                    fallback = result;
            }
            if (fallback is not null)
                return fallback with { Status = truncated ? CrawlStatus.Incomplete : CrawlStatus.Succeeded,
                    Diagnostic = truncated ? "Search incomplete; candidate is not an exact GTIN match." : "Candidate is not an exact GTIN match." };
            return Result(truncated ? CrawlStatus.Incomplete : CrawlStatus.NotFound,
                truncated ? "Discovery limited, disallowed, or some pages lacked supported product data." : "No matching product found.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or XmlException or InvalidDataException or
            TaskCanceledException or RegexMatchTimeoutException)
        {
            return Result(CrawlStatus.Failed, $"Platou Sport discovery failed: {ex.Message}");
        }
        finally { gate.Release(); }
    }

    private static CrawlResult Result(CrawlStatus status, string diagnostic) => new(status, [], []) { Diagnostic = diagnostic };

    public static IReadOnlyList<Uri> ParseSitemap(string xml, string root, string entry)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        if (document.Root?.Name != SitemapNamespace + root)
            throw new InvalidDataException("Unexpected sitemap root.");
        return document.Root.Elements(SitemapNamespace + entry)
            .Select(e => (string?)e.Element(SitemapNamespace + "loc"))
            .Select(s => Uri.TryCreate(s, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null && SameOrigin(uri))
            .Select(uri => uri!).ToArray();
    }

    private static bool SameOrigin(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals(Origin.Host, StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsProductSitemap(Uri uri) =>
        Regex.IsMatch(uri.AbsolutePath, @"^/product-sitemap(?:[0-9]+)?\.xml$", RegexOptions.CultureInvariant);
    private static bool IsProductPage(Uri uri) =>
        uri.AbsolutePath.StartsWith("/produkt/", StringComparison.Ordinal) && uri.AbsolutePath.Length > "/produkt/".Length;

    private sealed class RobotsRules
    {
        private readonly List<(bool Allow, string Pattern)> rules = [];
        public TimeSpan Delay { get; private set; }

        public static RobotsRules Parse(string text)
        {
            var groups = new List<(List<string> Agents, List<(string Key, string Value)> Directives)>();
            var agents = new List<string>();
            var directives = new List<(string, string)>();
            foreach (var line in text.Split('\n'))
            {
                var part = line.Split('#', 2)[0];
                var colon = part.IndexOf(':');
                if (colon < 0) continue;
                var key = part[..colon].Trim().ToLowerInvariant();
                var value = part[(colon + 1)..].Trim();
                if (key == "user-agent")
                {
                    if (directives.Count > 0) { groups.Add((agents, directives)); agents = []; directives = []; }
                    agents.Add(value);
                }
                else if (agents.Count > 0 && (key == "allow" || key == "disallow" || key == "crawl-delay"))
                    directives.Add((key, value));
            }
            if (agents.Count > 0) groups.Add((agents, directives));
            var best = groups.SelectMany(g => g.Agents)
                .Where(a => a == "*" || (a.Length > 0 && Agent.Contains(a, StringComparison.OrdinalIgnoreCase)))
                .Select(a => a == "*" ? 0 : a.Length).DefaultIfEmpty(-1).Max();
            var result = new RobotsRules();
            foreach (var group in groups.Where(g => best >= 0 && g.Agents.Any(a => (a == "*" ? 0 :
                Agent.Contains(a, StringComparison.OrdinalIgnoreCase) ? a.Length : -1) == best)))
                foreach (var (key, value) in group.Directives)
                {
                    if ((key == "allow" || key == "disallow") && value.Length > 0)
                        result.rules.Add((key == "allow", value));
                    if (key == "crawl-delay")
                    {
                        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                            out var seconds) || seconds < 0 || seconds > 3600)
                            throw new InvalidDataException("Unsupported robots.txt crawl-delay.");
                        result.Delay = TimeSpan.FromSeconds(Math.Max(result.Delay.TotalSeconds, seconds));
                    }
                }
            return result;
        }

        public bool Allows(Uri uri)
        {
            var path = uri.PathAndQuery;
            var matching = rules.Where(r => Regex.IsMatch(path,
                "^" + Regex.Escape(r.Pattern).Replace(@"\*", ".*").Replace(@"\$", "$"),
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                .OrderByDescending(r => r.Pattern.Replace("*", "").TrimEnd('$').Length)
                .ThenByDescending(r => r.Allow).FirstOrDefault();
            return matching == default || matching.Allow;
        }
    }
}
