using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Stadion.SmartDrift.PriceMonitor.Core;

namespace Stadion.SmartDrift.PriceMonitor.Crawler;

public sealed record BrekkeSportOptions(int MaxSitemaps = 30, int MaxProductPages = 500, int MaxResponseBytes = 2_000_000,
    TimeSpan? RequestInterval = null, TimeSpan? RequestTimeout = null);

public sealed class BrekkeSportCrawler(HttpClient client, BrekkeSportOptions? options = null) : IPollingWebsiteCrawler
{
    private const string Agent = "StadionSmartDriftPriceMonitor";
    private static readonly Uri Origin = new("https://www.brekkesport.no/");
    private static readonly XNamespace SitemapNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";
    private readonly BrekkeSportOptions settings = options ?? new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset? lastRequest;

    public string Key => "brekke-sport";

    public Task<CrawlResult> CrawlAsync(CrawlTarget target, CancellationToken cancellationToken) =>
        ExecuteAsync(target, null, cancellationToken);

    public Task<CrawlResult> PollAsync(CrawlTarget target, VerifiedProductMatch match, CancellationToken cancellationToken) =>
        ExecuteAsync(target, match, cancellationToken);

    private async Task<CrawlResult> ExecuteAsync(CrawlTarget target, VerifiedProductMatch? match, CancellationToken cancellationToken)
    {
        if (settings.MaxSitemaps <= 0 || settings.MaxProductPages <= 0 || settings.MaxResponseBytes <= 0 ||
            settings.RequestInterval < TimeSpan.Zero || settings.RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (target.Variants.Count == 0 || target.Variants.Select(v => v.Id).Distinct().Count() != target.Variants.Count)
            return Result(CrawlStatus.Failed, "Target variants must have distinct IDs and at least one variant is required.");
        if (match is not null && (!SameOrigin(match.ProductUrl) || !IsProductPage(match.ProductUrl) ||
            string.IsNullOrWhiteSpace(match.ExternalProductId)))
            return Result(CrawlStatus.Failed, "RequiresRequalification: persisted product URL or identity is not usable.");

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
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(settings.RequestTimeout ?? TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd($"{Agent}/1.0");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > settings.MaxResponseBytes)
                    throw new InvalidDataException("Response exceeds the size limit.");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream();
                var bytes = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(bytes, timeout.Token)) > 0)
                {
                    if (buffer.Length + count > settings.MaxResponseBytes)
                        throw new InvalidDataException("Response exceeds the size limit.");
                    buffer.Write(bytes, 0, count);
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }

            var robots = RobotsRules.Parse(await Fetch(new Uri(Origin, "robots.txt")));
            if (robots.Delay > interval) interval = robots.Delay;
            if (match is not null)
            {
                if (!robots.Allows(match.ProductUrl))
                    return Result(CrawlStatus.Failed, "RequiresRequalification: persisted product URL is disallowed by robots.txt.");
                var result = BrekkeSportProductParser.Parse(await Fetch(match.ProductUrl), match.ProductUrl, target, DateTimeOffset.UtcNow);
                if (result.ProductMatches.Count != 1 || result.ProductMatches[0].ExternalProductId != match.ExternalProductId)
                    return Result(CrawlStatus.Failed, "RequiresRequalification: persisted product identity is no longer verified.");
                if (result.Status != CrawlStatus.Succeeded)
                {
                    if (target.Variants.Any(input => match.Variants.Any(v => v.VariantId == input.Id)))
                        return Result(CrawlStatus.Failed, "RequiresRequalification: a previously verified selected variant is no longer present or priced.");
                    return result;
                }
                if (target.Variants.Any(input =>
                {
                    var expected = match.Variants.Where(v => v.VariantId == input.Id).ToArray();
                    var actual = result.VariantMatches.Where(v => v.VariantId == input.Id).ToArray();
                    return expected.Length > 1 || actual.Length != 1 || actual[0].Gtin != input.Gtin?.Trim() ||
                        expected.Length == 1 && (expected[0].Gtin != actual[0].Gtin ||
                            expected[0].Gtin != input.Gtin?.Trim() || expected[0].ExternalVariantId != actual[0].ExternalVariationId);
                }))
                    return Result(CrawlStatus.Failed, "RequiresRequalification: persisted product/variant identity is no longer present or priced unambiguously.");
                return result;
            }

            IReadOnlyList<Uri> sitemapUrls = robots.Sitemaps.Count > 0 ? robots.Sitemaps : [new Uri(Origin, "sitemap.xml")];
            var allowedSitemaps = sitemapUrls.Where(robots.Allows).ToArray();
            var pending = new Queue<Uri>(allowedSitemaps);
            var sitemaps = new HashSet<Uri>();
            var pages = new HashSet<Uri>();
            var truncated = allowedSitemaps.Length != sitemapUrls.Count;
            while (pending.Count > 0 && sitemaps.Count < settings.MaxSitemaps && pages.Count < 100_000)
            {
                var sitemap = pending.Dequeue();
                if (!sitemaps.Add(sitemap)) continue;
                if (!robots.Allows(sitemap)) { truncated = true; continue; }
                var (isIndex, urls) = ParseSitemap(await Fetch(sitemap));
                foreach (var url in urls)
                {
                    if (isIndex)
                    {
                        if (!sitemaps.Contains(url)) pending.Enqueue(url);
                    }
                    else if (IsProductPage(url))
                    {
                        if (pages.Count >= 100_000) { truncated = true; break; }
                        pages.Add(url);
                    }
                }
            }
            if (pending.Count > 0) truncated = true;
            var terms = Regex.Matches(target.ProductName ?? "", @"[a-zA-Z0-9]{3,}")
                .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var ordered = pages.OrderByDescending(url => terms.Count(term =>
                Uri.UnescapeDataString(url.AbsolutePath).Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
            truncated |= ordered.Length > settings.MaxProductPages;
            var products = new List<ProductMatchCandidate>();
            var variants = new List<ProductVariantMatchCandidate>();
            var observations = new List<PriceObservationCandidate>();
            foreach (var url in ordered.Take(settings.MaxProductPages))
            {
                if (!robots.Allows(url)) { truncated = true; continue; }
                var result = BrekkeSportProductParser.Parse(await Fetch(url), url, target, DateTimeOffset.UtcNow);
                if (result.Status == CrawlStatus.Ambiguous) return result;
                if (result.VariantMatches.Count > 0)
                {
                    products.AddRange(result.ProductMatches);
                    variants.AddRange(result.VariantMatches);
                    observations.AddRange(result.PriceObservations);
                    if (target.Variants.All(v => observations.Any(o => o.VariantId == v.Id)))
                    {
                        var unique = products.DistinctBy(p => p.ExternalProductId, StringComparer.Ordinal).ToArray();
                        return unique.Length == 1
                            ? new CrawlResult(CrawlStatus.Succeeded, unique, observations) { VariantMatches = variants }
                            : Result(CrawlStatus.Ambiguous, "Target variants resolved to multiple external products.");
                    }
                }
                if (result.Status == CrawlStatus.Incomplete) truncated = true;
            }
            if (variants.Count > 0)
                return new CrawlResult(CrawlStatus.Incomplete, products, observations)
                {
                    VariantMatches = variants,
                    Diagnostic = "Some target variants are unmatched or have no unambiguous priced offer."
                };
            return Result(truncated ? CrawlStatus.Incomplete : CrawlStatus.NotFound,
                truncated ? "Discovery was limited, disallowed, or some pages lacked supported product data." : "No matching product found.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or XmlException or InvalidDataException or
            OperationCanceledException or RegexMatchTimeoutException)
        {
            return Result(CrawlStatus.Failed, $"Brekke Sport {(match is null ? "discovery" : "polling (RequiresRequalification)")} failed: {ex.Message}");
        }
        finally { gate.Release(); }
    }

    public static IReadOnlyList<Uri> ParseSitemap(string xml, string root, string entry)
    {
        var (isIndex, urls) = ParseSitemap(xml);
        if (root == "sitemapindex" && entry == "sitemap" && isIndex ||
            root == "urlset" && entry == "url" && !isIndex)
            return urls;
        throw new InvalidDataException("Unexpected sitemap root.");
    }

    private static (bool IsIndex, IReadOnlyList<Uri> Urls) ParseSitemap(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        if (document.Root?.Name == SitemapNamespace + "sitemapindex")
            return (true, SitemapUrls(document.Root, "sitemap"));
        if (document.Root?.Name == SitemapNamespace + "urlset")
            return (false, SitemapUrls(document.Root, "url"));
        throw new InvalidDataException("Unexpected sitemap root.");
    }

    private static IReadOnlyList<Uri> SitemapUrls(XElement root, string entry) =>
        root.Elements(SitemapNamespace + entry)
            .Select(e => (string?)e.Element(SitemapNamespace + "loc"))
            .Select(s => Uri.TryCreate(s, UriKind.Absolute, out var uri) && SameOrigin(uri) ? uri : null)
            .Where(uri => uri is not null)
            .Select(uri => uri!)
            .ToArray();

    private static bool SameOrigin(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals(Origin.Host, StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsProductPage(Uri uri) => SameOrigin(uri) &&
        uri.AbsolutePath.Length > 1 && !uri.AbsolutePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private static CrawlResult Result(CrawlStatus status, string diagnostic) => new(status, [], []) { Diagnostic = diagnostic };

    private sealed class RobotsRules
    {
        private readonly List<(bool Allow, string Pattern)> rules = [];
        public List<Uri> Sitemaps { get; } = [];
        public TimeSpan Delay { get; private set; }

        public static RobotsRules Parse(string text)
        {
            var groups = new List<(List<string> Agents, List<(string Key, string Value)> Directives)>();
            var agents = new List<string>();
            var directives = new List<(string Key, string Value)>();
            var sitemapLines = new List<string>();
            foreach (var line in text.Split('\n'))
            {
                var part = line.Split('#', 2)[0];
                var colon = part.IndexOf(':');
                if (colon < 0) continue;
                var key = part[..colon].Trim().ToLowerInvariant();
                var value = part[(colon + 1)..].Trim();
                if (key == "sitemap") { sitemapLines.Add(value); continue; }
                if (key == "user-agent")
                {
                    if (directives.Count > 0) { groups.Add((agents, directives)); agents = []; directives = []; }
                    agents.Add(value);
                }
                else if (agents.Count > 0 && (key == "allow" || key == "disallow" || key == "crawl-delay"))
                    directives.Add((key, value));
            }
            if (agents.Count > 0) groups.Add((agents, directives));

            var result = new RobotsRules();
            foreach (var sitemap in sitemapLines)
                if (Uri.TryCreate(sitemap, UriKind.Absolute, out var uri) && SameOrigin(uri))
                    result.Sitemaps.Add(uri);
            var best = groups.SelectMany(g => g.Agents)
                .Where(a => a == "*" || (a.Length > 0 && Agent.Contains(a, StringComparison.OrdinalIgnoreCase)))
                .Select(a => a == "*" ? 0 : a.Length).DefaultIfEmpty(-1).Max();
            foreach (var group in groups.Where(g => best >= 0 && g.Agents.Any(a =>
                (a == "*" ? 0 : Agent.Contains(a, StringComparison.OrdinalIgnoreCase) ? a.Length : -1) == best)))
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
            var matching = rules.Where(r => Regex.IsMatch(uri.PathAndQuery,
                    "^" + Regex.Escape(r.Pattern).Replace(@"\*", ".*").Replace(@"\$", "$"),
                    RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                .OrderByDescending(r => r.Pattern.Replace("*", "").TrimEnd('$').Length)
                .ThenByDescending(r => r.Allow)
                .FirstOrDefault();
            return matching == default || matching.Allow;
        }
    }
}
