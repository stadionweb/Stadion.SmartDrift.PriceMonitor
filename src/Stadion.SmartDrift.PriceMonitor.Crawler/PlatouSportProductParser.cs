using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Stadion.SmartDrift.PriceMonitor.Core;

namespace Stadion.SmartDrift.PriceMonitor.Crawler;

public static class PlatouSportProductParser
{
    private static readonly Regex JsonLd = new(
        """<script\b(?=[^>]*\btype\s*=\s*['"]application/ld\+json['"])[^>]*>(.*?)</script\s*>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static CrawlResult Parse(string html, Uri sourceUrl, CrawlTarget target, DateTimeOffset observedAt)
    {
        CrawlResult? fallback = null;
        foreach (Match script in JsonLd.Matches(html))
        {
            try
            {
                using var document = JsonDocument.Parse(script.Groups[1].Value);
                foreach (var product in Products(document.RootElement))
                {
                    var result = MatchProduct(product, sourceUrl, target, observedAt);
                    if (result?.ProductMatches[0].MatchBasis == "gtin")
                        return result;
                    if (result is not null && (fallback is null ||
                        result.ProductMatches[0].MatchBasis == "article-number"))
                        fallback = result;
                }
            }
            catch (JsonException)
            {
                // A page may contain unrelated or malformed structured data.
            }
        }

        return fallback ?? new CrawlResult(CrawlStatus.NotFound, [], []);
    }

    private static IEnumerable<JsonElement> Products(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var product in Products(item))
                    yield return product;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (IsType(element, "Product") || IsType(element, "ProductGroup"))
                yield return element;
            if (element.TryGetProperty("@graph", out var graph))
                foreach (var product in Products(graph))
                    yield return product;
        }
    }

    private static CrawlResult? MatchProduct(JsonElement product, Uri url, CrawlTarget target, DateTimeOffset observedAt)
    {
        var name = Text(product, "name");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var variants = new List<JsonElement> { product };
        if (product.TryGetProperty("hasVariant", out var children))
        {
            if (children.ValueKind == JsonValueKind.Array)
                variants.AddRange(children.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object));
            else if (children.ValueKind == JsonValueKind.Object)
                variants.Add(children);
        }

        foreach (var variant in variants)
        {
            var gtin = Text(variant, "gtin") ?? Text(variant, "gtin13") ?? Text(variant, "gtin14") ??
                Text(variant, "gtin12") ?? Text(variant, "gtin8");
            if (string.IsNullOrWhiteSpace(gtin))
                continue;
            var matches = target.Variants.Where(v => !string.IsNullOrWhiteSpace(v.Gtin) && v.Gtin == gtin).ToArray();
            if (matches.Length != 1)
                continue;

            var match = new ProductMatchCandidate(name, url, gtin, Text(variant, "mpn") ?? Text(product, "mpn"))
            {
                MatchBasis = "gtin", VariantId = matches[0].Id,
                Size = Text(variant, "size"), Color = Text(variant, "color")
            };
            var observation = Offer(variant, url, observedAt, matches[0].Id);
            return new CrawlResult(observation is null ? CrawlStatus.Incomplete : CrawlStatus.Succeeded,
                [match], observation is null ? [] : [observation])
            {
                Diagnostic = observation is null ? "Exact variant found, but no unambiguous priced offer was available." : null
            };
        }

        var article = Text(product, "mpn");
        var brand = Text(product, "brand");
        if (product.TryGetProperty("brand", out var brandObject) && brandObject.ValueKind == JsonValueKind.Object)
            brand = Text(brandObject, "name");
        var articleMatch = EqualIdentity(article, target.ArticleNumber);
        var nameMatch = EqualIdentity(brand, target.Brand) && EqualIdentity(name, target.ProductName);
        if (!articleMatch && !nameMatch)
            return null;

        return new CrawlResult(CrawlStatus.Succeeded,
            [new ProductMatchCandidate(name, url, null, article) { MatchBasis = articleMatch ? "article-number" : "brand-name" }], []);
    }

    private static PriceObservationCandidate? Offer(JsonElement product, Uri url, DateTimeOffset observedAt, Guid variantId)
    {
        if (!product.TryGetProperty("offers", out var offer))
            return null;
        if (offer.ValueKind == JsonValueKind.Array)
        {
            var offers = offer.EnumerateArray().ToArray();
            if (offers.Length != 1)
                return null;
            offer = offers[0];
        }
        if (offer.ValueKind != JsonValueKind.Object || !IsType(offer, "Offer") ||
            !decimal.TryParse(Text(offer, "price"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price) ||
            price < 0 || string.IsNullOrWhiteSpace(Text(offer, "priceCurrency")))
            return null;

        var availability = Text(offer, "availability")?.Split('/').Last();
        bool? inStock = availability switch { "InStock" => true, "OutOfStock" => false, _ => null };
        return new PriceObservationCandidate(null, price, null, null, Text(offer, "priceCurrency"),
            inStock, availability, null, url, observedAt) { VariantId = variantId };
    }

    private static bool IsType(JsonElement element, string type)
    {
        if (!element.TryGetProperty("@type", out var value)) return false;
        return value.ValueKind == JsonValueKind.String ? value.GetString() == type :
            value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == type);
    }

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static bool EqualIdentity(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
