using System.Globalization;
using System.Net;
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
    private static readonly Regex Forms = new(
        """<form\b(?:[^"'<>]|"[^"]*"|'[^']*')*>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Attributes = new(
        """(?:^|\s)(?<name>[a-zA-Z_:][\w:.-]*)\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)')""",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static CrawlResult Parse(string html, Uri sourceUrl, CrawlTarget target, DateTimeOffset observedAt)
    {
        if (!ValidTarget(target))
            return new CrawlResult(CrawlStatus.Failed, [], []) { Diagnostic = "Target variants must have distinct IDs and at least one variant is required." };
        var woo = ParseWooCommerce(html, sourceUrl, target, observedAt);
        if (woo is not null) return woo;
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

        private static CrawlResult? ParseWooCommerce(string html, Uri url, CrawlTarget target, DateTimeOffset observedAt)
        {
            var forms = Forms.Matches(html).Cast<Match>()
                .Select(form => Attributes.Matches(form.Value).Cast<Match>()
                    .GroupBy(a => a.Groups["name"].Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.First().Groups["value"].Value), StringComparer.OrdinalIgnoreCase))
                .Where(a => a.ContainsKey("data-product_variations")).ToArray();
            if (forms.Length == 0) return null;
            if (forms.Length != 1)
                return new CrawlResult(CrawlStatus.Ambiguous, [], []) { Diagnostic = "Multiple variation forms found." };
            var form = forms[0];
            if (!form.TryGetValue("data-product_id", out var productId) || string.IsNullOrWhiteSpace(productId))
                return new CrawlResult(CrawlStatus.Incomplete, [], []) { Diagnostic = "Variation form lacks product ID." };

            var products = new List<JsonElement>();
            foreach (Match script in JsonLd.Matches(html))
            {
                try
                {
                    using var document = JsonDocument.Parse(script.Groups[1].Value);
                    products.AddRange(Products(document.RootElement).Where(p =>
                        Uri.TryCreate(Text(p, "url"), UriKind.Absolute, out var productUrl) && productUrl == url)
                        .Select(p => p.Clone()));
                }
                catch (JsonException) { }
            }
            if (products.Count != 1)
                return new CrawlResult(products.Count > 1 ? CrawlStatus.Ambiguous : CrawlStatus.Incomplete, [], [])
                    { Diagnostic = "Could not uniquely associate JSON-LD product with variation form." };
            var product = products[0];
            var name = Text(product, "name");
            if (string.IsNullOrWhiteSpace(name))
                return new CrawlResult(CrawlStatus.Incomplete, [], []) { Diagnostic = "Product name is missing." };
            var candidate = new ProductMatchCandidate(name, url, null, Text(product, "mpn"))
                { MatchBasis = "gtin", ExternalProductId = productId, ExternalSku = Text(product, "sku") };

            try
            {
                using var document = JsonDocument.Parse(form["data-product_variations"]);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return new CrawlResult(CrawlStatus.Incomplete, [], []) { Diagnostic = "Variation payload is unavailable." };
                var items = document.RootElement.EnumerateArray().ToArray();
                if (items.Any(v => v.ValueKind != JsonValueKind.Object))
                    return new CrawlResult(CrawlStatus.Incomplete, [], []) { Diagnostic = "Malformed variation payload." };
                var matches = new List<ProductVariantMatchCandidate>();
                var observations = new List<PriceObservationCandidate>();
                var currency = ProductCurrency(product);
                foreach (var targetVariant in target.Variants)
                {
                    if (string.IsNullOrWhiteSpace(targetVariant.Gtin)) continue;
                    var matching = items.Where(v => Text(v, "sku") == targetVariant.Gtin).ToArray();
                    if (matching.Length > 1 || target.Variants.Count(v => v.Gtin == targetVariant.Gtin) > 1)
                        return new CrawlResult(CrawlStatus.Ambiguous, [candidate], []) { Diagnostic = "Duplicate variant GTIN." };
                    if (matching.Length == 0) continue;
                    var variation = matching[0];
                    var size = variation.TryGetProperty("attributes", out var attributes) ? Text(attributes, "attribute_pa_size") : null;
                    var color = variation.TryGetProperty("attributes", out attributes) ? Text(attributes, "attribute_pa_color") : null;
                    if (Conflicts(size, targetVariant.Size) || Conflicts(color, targetVariant.Color)) continue;
                    matches.Add(new ProductVariantMatchCandidate(targetVariant.Id, url, targetVariant.Gtin, size, color, VariantMatchMethod.ExactGtin)
                        { ExternalVariationId = Text(variation, "variation_id") });
                    if (decimal.TryParse(Text(variation, "display_price"), NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var price) && price >= 0 && currency is not null)
                    {
                        decimal? regular = decimal.TryParse(Text(variation, "display_regular_price"), NumberStyles.AllowDecimalPoint,
                            CultureInfo.InvariantCulture, out var parsedRegular) && parsedRegular >= 0 ? parsedRegular : null;
                        bool? stock = variation.TryGetProperty("is_in_stock", out var inStock) ?
                            inStock.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;
                        observations.Add(new PriceObservationCandidate(regular, price, null, null, currency, stock,
                            null, null, url, observedAt) { VariantId = targetVariant.Id });
                    }
                }
                if (matches.Count == 0)
                    return new CrawlResult(items.Length == 0 || items.Any(v => string.IsNullOrWhiteSpace(Text(v, "sku")))
                        ? CrawlStatus.Incomplete : CrawlStatus.NotFound, [candidate], []);
                return new CrawlResult(observations.Count == target.Variants.Count ? CrawlStatus.Succeeded : CrawlStatus.Incomplete,
                    [candidate], observations)
                {
                    VariantMatches = matches,
                    Diagnostic = observations.Count == target.Variants.Count ? null : "Some target variants are unmatched or lack a priced offer."
                };
            }
            catch (JsonException)
            {
                return new CrawlResult(CrawlStatus.Incomplete, [], []) { Diagnostic = "Malformed variation payload." };
            }
        }

        private static string? ProductCurrency(JsonElement product)
        {
            if (!product.TryGetProperty("offers", out var offers)) return null;
            var items = offers.ValueKind == JsonValueKind.Array ? offers.EnumerateArray().ToArray() : [offers];
            if (items.Length == 0 || items.Any(o => o.ValueKind != JsonValueKind.Object || !IsType(o, "Offer") ||
                string.IsNullOrWhiteSpace(Text(o, "priceCurrency")))) return null;
            var currencies = items.Select(o => Text(o, "priceCurrency")).Distinct(StringComparer.Ordinal).ToArray();
            return currencies.Length == 1 ? currencies[0] : null;
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

    private static PriceObservationCandidate? Offer(JsonElement product, Uri url, DateTimeOffset observedAt, long variantId)
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
