using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Stadion.SmartDrift.PriceMonitor.Core;

namespace Stadion.SmartDrift.PriceMonitor.Crawler;

public static class BrekkeSportProductParser
{
    private static readonly Regex JsonLd = new(
        """<script\b(?=[^>]*\btype\s*=\s*['"]application/ld\+json['"])[^>]*>(.*?)</script\s*>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static CrawlResult Parse(string html, Uri sourceUrl, CrawlTarget target, DateTimeOffset observedAt)
    {
        if (target.Variants.Count == 0 || target.Variants.Select(v => v.Id).Distinct().Count() != target.Variants.Count)
            return Result(CrawlStatus.Failed, "Target variants must have distinct IDs and at least one variant is required.");

        var products = new List<JsonElement>();
        foreach (Match script in JsonLd.Matches(html))
        {
            try
            {
                using var document = JsonDocument.Parse(WebUtility.HtmlDecode(script.Groups[1].Value));
                products.AddRange(FindProducts(document.RootElement).Where(p => ProductUrlMatches(p, sourceUrl)).Select(p => p.Clone()));
            }
            catch (JsonException)
            {
                // A page may contain unrelated or malformed structured data.
            }
        }

        if (products.Count == 0)
            return Result(CrawlStatus.Incomplete, "Page does not contain supported nopCommerce product data.");

        var candidates = new List<ProductMatchCandidate>();
        var matches = new List<ProductVariantMatchCandidate>();
        var observations = new List<PriceObservationCandidate>();
        var matchedTargetIds = new HashSet<long>();
        var sawGtin = false;
        foreach (var product in products)
        {
            var variants = Variants(product).ToArray();
            var gtins = variants.Select(Gtin).Where(gtin => !string.IsNullOrWhiteSpace(gtin)).ToArray();
            sawGtin |= gtins.Length > 0;
            foreach (var input in target.Variants)
            {
                if (string.IsNullOrWhiteSpace(input.Gtin)) continue;
                var matching = variants.Where(variant => Gtin(variant) == input.Gtin.Trim()).ToArray();
                if (matching.Length == 0) continue;
                if (matching.Length > 1 || target.Variants.Count(v => v.Gtin?.Trim() == input.Gtin.Trim()) > 1 ||
                    !matchedTargetIds.Add(input.Id))
                    return Result(CrawlStatus.Ambiguous, "Duplicate exact GTIN evidence on the product page.");

                var variant = matching[0];
                var size = Text(variant, "size");
                var color = Text(variant, "color");
                if (Conflicts(size, input.Size) || Conflicts(color, input.Color))
                    continue;

                var externalProductId = Text(product, "productID") ?? Text(product, "sku");
                var name = Text(product, "name");
                if (string.IsNullOrWhiteSpace(externalProductId) || string.IsNullOrWhiteSpace(name))
                    return Result(CrawlStatus.Incomplete, "Product data lacks a stable ID or name.");

                var externalVariationId = Text(variant, "productID") ?? Text(variant, "sku");
                candidates.Add(new ProductMatchCandidate(name, sourceUrl, null, Text(product, "mpn"))
                {
                    MatchBasis = "gtin",
                    ExternalProductId = externalProductId,
                    ExternalSku = Text(product, "sku")
                });
                matches.Add(new ProductVariantMatchCandidate(input.Id, sourceUrl, input.Gtin.Trim(), size, color,
                    VariantMatchMethod.ExactGtin) { ExternalVariationId = externalVariationId });
                var observation = Offer(variant, sourceUrl, observedAt, input.Id);
                if (observation is not null) observations.Add(observation);
            }
        }

        if (matches.Count == 0)
            return Result(sawGtin ? CrawlStatus.NotFound : CrawlStatus.Incomplete,
                sawGtin ? "No target GTIN matched the product page." : "Product data does not expose variant GTINs.");

        var uniqueProducts = candidates.DistinctBy(p => p.ExternalProductId, StringComparer.Ordinal).ToArray();
        if (uniqueProducts.Length != 1)
            return Result(CrawlStatus.Ambiguous, "Target GTINs resolved to multiple external products.");
        return new CrawlResult(observations.Count == target.Variants.Count ? CrawlStatus.Succeeded : CrawlStatus.Incomplete,
            uniqueProducts, observations)
        {
            VariantMatches = matches,
            Diagnostic = observations.Count == target.Variants.Count ? null : "Some target variants are unmatched or lack a priced offer."
        };
    }

    private static IEnumerable<JsonElement> FindProducts(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var product in FindProducts(item))
                    yield return product;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (IsType(element, "Product") || IsType(element, "ProductGroup"))
                yield return element;
            if (element.TryGetProperty("@graph", out var graph))
                foreach (var product in FindProducts(graph))
                    yield return product;
        }
    }

    private static IEnumerable<JsonElement> Variants(JsonElement product)
    {
        if (product.TryGetProperty("hasVariant", out var children))
        {
            if (children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object))
                    yield return child;
            }
            else if (children.ValueKind == JsonValueKind.Object)
            {
                yield return children;
            }
            yield break;
        }

        yield return product;
    }

    private static PriceObservationCandidate? Offer(JsonElement product, Uri url, DateTimeOffset observedAt, long variantId)
    {
        if (!product.TryGetProperty("offers", out var offer)) return null;
        if (offer.ValueKind == JsonValueKind.Array)
        {
            var offers = offer.EnumerateArray().ToArray();
            if (offers.Length != 1) return null;
            offer = offers[0];
        }

        if (offer.ValueKind != JsonValueKind.Object || !IsType(offer, "Offer") ||
            !decimal.TryParse(Text(offer, "price"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price) ||
            price < 0 || Text(offer, "priceCurrency") is not { Length: 3 } currency ||
            !currency.All(c => c is >= 'A' and <= 'Z'))
            return null;

        var availability = Text(offer, "availability")?.Split('/').Last();
        bool? inStock = availability switch { "InStock" => true, "OutOfStock" => false, _ => null };
        return new PriceObservationCandidate(null, price, null, null, currency, inStock, availability, null, url, observedAt)
            { VariantId = variantId };
    }

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

    private static bool IsType(JsonElement element, string type) =>
        element.TryGetProperty("@type", out var value) &&
        (value.ValueKind == JsonValueKind.String && value.GetString() == type ||
         value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == type));

    private static bool ProductUrlMatches(JsonElement product, Uri sourceUrl)
    {
        var value = Text(product, "url");
        return value is null || sourceUrl.IsAbsoluteUri && sourceUrl.Scheme == Uri.UriSchemeHttps &&
            Uri.TryCreate(value, UriKind.Absolute, out var productUrl) && productUrl.Scheme == Uri.UriSchemeHttps &&
            productUrl.Host.Equals(sourceUrl.Host, StringComparison.OrdinalIgnoreCase) && productUrl.IsDefaultPort &&
            string.IsNullOrEmpty(productUrl.UserInfo) && string.IsNullOrEmpty(productUrl.Query) &&
            string.IsNullOrEmpty(productUrl.Fragment) && productUrl.AbsolutePath == sourceUrl.AbsolutePath;
    }

    private static bool Conflicts(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        !string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static CrawlResult Result(CrawlStatus status, string diagnostic) =>
        new(status, [], []) { Diagnostic = diagnostic };
}
