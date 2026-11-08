namespace Stadion.SmartDrift.PriceMonitor.Core;

public static class MonitoringResultValidator
{
    public static void Validate(MonitoringWorkItem work, CrawlResult result)
    {
        if (work.TargetId <= 0 || work.SubscriptionId <= 0 || work.WebsiteId <= 0 || work.Target.ProductId <= 0 ||
            work.Target.Variants.Count == 0 || work.Target.Variants.Any(v => v.Id <= 0) ||
            work.Target.Variants.Select(v => v.Id).Distinct().Count() != work.Target.Variants.Count)
            throw new InvalidDataException("Persistence requires real, distinct monitoring identities.");
        if (result.Status != CrawlStatus.Succeeded || result.ProductMatches.Count != 1)
            throw new InvalidDataException("Persistence requires one unambiguous successful external product.");

        var product = result.ProductMatches[0];
        if (string.IsNullOrWhiteSpace(product.ExternalProductId) || product.ExternalProductId.Length > 200 ||
            product.Name.Length > 500 || !ValidUrl(product.SourceUrl))
            throw new InvalidDataException("External product identity or URL is invalid.");
        if (result.VariantMatches.Count != work.Target.Variants.Count ||
            result.PriceObservations.Count != work.Target.Variants.Count)
            throw new InvalidDataException("Every selected variant requires one exact match and one observation.");

        foreach (var input in work.Target.Variants)
        {
            var matches = result.VariantMatches.Where(v => v.VariantId == input.Id).ToArray();
            var observations = result.PriceObservations.Where(o => o.VariantId == input.Id).ToArray();
            if (matches.Length != 1 || observations.Length != 1)
                throw new InvalidDataException("Variant result identities are missing or duplicated.");
            var variant = matches[0];
            var observation = observations[0];
            if (variant.MatchMethod != VariantMatchMethod.ExactGtin || string.IsNullOrWhiteSpace(input.Gtin) ||
                variant.Gtin != input.Gtin.Trim() || variant.Gtin.Length > 20 ||
                variant.ProductUrl != product.SourceUrl || observation.SourceUrl != product.SourceUrl ||
                variant.ExternalVariationId?.Length > 200 || variant.Size?.Length > 100 || variant.Color?.Length > 200)
                throw new InvalidDataException("Variant identity does not belong to the selected product.");
            if (observation.CurrentPrice is null || !ValidPrice(observation.CurrentPrice) ||
                !ValidPrice(observation.RegularPrice) || !ValidPrice(observation.PreviousPrice) || !ValidPrice(observation.MemberPrice) ||
                observation.CurrencyCode is not { Length: 3 } currency || !currency.All(c => c is >= 'A' and <= 'Z') ||
                observation.ObservedAt == default || observation.StockText?.Length > 500 || observation.PromotionText?.Length > 1000)
                throw new InvalidDataException("Observation is not valid for the database contract.");
        }
    }

    private static bool ValidUrl(Uri url) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(url.UserInfo) && url.AbsoluteUri.Length <= 2000;

    private static bool ValidPrice(decimal? price) => price is null ||
        (price >= 0 && price < 10_000_000_000_000_000m && decimal.Round(price.Value, 2) == price.Value);
}
