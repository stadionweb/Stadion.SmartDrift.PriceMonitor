namespace Stadion.SmartDrift.PriceMonitor.Core;

public sealed record CrawlVariant(long Id, string? Gtin, string? ArticleNumber, string? Size, string? Color);

public sealed record CrawlTarget(
    long ProductId,
    string? Brand,
    string? ProductName,
    string? ArticleNumber,
    IReadOnlyList<CrawlVariant> Variants);

public sealed record ProductMatchCandidate(string Name, Uri SourceUrl, string? Gtin, string? ArticleNumber)
{
    public string? MatchBasis { get; init; }
    public string? ExternalProductId { get; init; }
    public string? ExternalSku { get; init; }
}

public enum VariantMatchMethod
{
    ExactGtin
}

public sealed record ProductVariantMatchCandidate(
    long VariantId,
    Uri ProductUrl,
    string Gtin,
    string? Size,
    string? Color,
    VariantMatchMethod MatchMethod)
{
    public string? ExternalVariationId { get; init; }
}

public sealed record PriceObservationCandidate(
    decimal? RegularPrice,
    decimal? CurrentPrice,
    decimal? PreviousPrice,
    decimal? MemberPrice,
    string? CurrencyCode,
    bool? InStock,
    string? StockText,
    string? PromotionText,
    Uri SourceUrl,
    DateTimeOffset ObservedAt)
{
    public long? VariantId { get; init; }
}

public enum CrawlStatus
{
    Succeeded,
    Failed,
    NotFound,
    Incomplete,
    Ambiguous
}

public sealed record CrawlResult(
    CrawlStatus Status,
    IReadOnlyList<ProductMatchCandidate> ProductMatches,
    IReadOnlyList<PriceObservationCandidate> PriceObservations)
{
    public string? Diagnostic { get; init; }
    public IReadOnlyList<ProductVariantMatchCandidate> VariantMatches { get; init; } = [];
}

public interface IWebsiteCrawler
{
    string Key { get; }

    Task<CrawlResult> CrawlAsync(CrawlTarget target, CancellationToken cancellationToken);
}

public interface IPollingWebsiteCrawler : IWebsiteCrawler
{
    Task<CrawlResult> PollAsync(CrawlTarget target, VerifiedProductMatch match, CancellationToken cancellationToken);
}
