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
        if (!ValidTarget(target))
            return new CrawlResult(CrawlStatus.Failed, [], []) { Diagnostic = "Target variants must have distinct IDs and at least one variant is required." };
        CrawlResult? fallback = null;
        var sawProduct = false;
        var insufficientIdentity = false;
        foreach (Match script in JsonLd.Matches(html))
        {
            try
            {
                using var document = JsonDocument.Parse(script.Groups[1].Value);
                foreach (var product in Products(document.RootElement))
                {
                    sawProduct = true;
                    insufficientIdentity |= !HasVariantGtins(product);
                    var result = MatchProduct(product, sourceUrl, target, observedAt);
                    if (result?.VariantMatches.Count > 0)
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

        return fallback ?? (sawProduct && !insufficientIdentity
            ? new CrawlResult(CrawlStatus.NotFound, [], [])
            : new CrawlResult(CrawlStatus.Incomplete, [], []) { Diagnostic = "Product data is missing or lacks variant GTINs." });
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

        var variantMatches = new List<ProductVariantMatchCandidate>();
        var observations = new List<PriceObservationCandidate>();
        var counts = variants.Select(Gtin).Where(gtin => !string.IsNullOrWhiteSpace(gtin))
            .GroupBy(gtin => gtin!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            var gtin = Gtin(variant);
            if (string.IsNullOrWhiteSpace(gtin))
                continue;
            if (counts[gtin] != 1)
                continue;
            var matches = target.Variants.Where(v => !string.IsNullOrWhiteSpace(v.Gtin) && v.Gtin == gtin).ToArray();
            if (matches.Length != 1)
                continue;
            var size = Text(variant, "size");
            var color = Text(variant, "color");
            if (Conflicts(size, matches[0].Size) || Conflicts(color, matches[0].Color))
                continue;

            if (variantMatches.Any(v => v.VariantId == matches[0].Id))
                continue;
            variantMatches.Add(new ProductVariantMatchCandidate(matches[0].Id, url, gtin, size, color, VariantMatchMethod.ExactGtin));
            var observation = Offer(variant, url, observedAt, matches[0].Id);
            if (observation is not null) observations.Add(observation);
        }

        if (variantMatches.Count > 0)
        {
            var complete = observations.Count == target.Variants.Count;
            return new CrawlResult(complete ? CrawlStatus.Succeeded : CrawlStatus.Incomplete,
                [new ProductMatchCandidate(name, url, null, Text(product, "mpn")) { MatchBasis = "gtin" }], observations)
            {
                VariantMatches = variantMatches,
                Diagnostic = complete ? null : "Some target variants are unmatched or have no unambiguous priced offer."
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

    private static bool HasVariantGtins(JsonElement product)
    {
        if (product.TryGetProperty("hasVariant", out var variants))
        {
            if (variants.ValueKind == JsonValueKind.Array)
            {
                var items = variants.EnumerateArray().ToArray();
                return items.Length > 0 && items.All(HasGtin);
            }
            return HasGtin(variants);
        }
        return HasGtin(product);
    }

    private static bool HasGtin(JsonElement product) =>
        !string.IsNullOrWhiteSpace(Gtin(product));

    private static string? Gtin(JsonElement product) =>
        Text(product, "gtin") ?? Text(product, "gtin13") ?? Text(product, "gtin14") ??
        Text(product, "gtin12") ?? Text(product, "gtin8");

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

    private static bool Conflicts(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && !EqualIdentity(left, right);

    internal static bool ValidTarget(CrawlTarget target) => target.Variants.Count > 0 &&
        target.Variants.Select(v => v.Id).Distinct().Count() == target.Variants.Count;
}
