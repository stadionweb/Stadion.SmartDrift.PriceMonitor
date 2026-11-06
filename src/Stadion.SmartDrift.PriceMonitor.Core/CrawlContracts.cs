namespace Stadion.SmartDrift.PriceMonitor.Core;

public sealed record CrawlVariant(Guid Id, string? Gtin, string? ArticleNumber, string? Size, string? Color);

public sealed record CrawlTarget(
    Guid ProductId,
    string? Brand,
    string? ProductName,
    string? ArticleNumber,
    IReadOnlyList<CrawlVariant> Variants);

public sealed record ProductMatchCandidate(string Name, Uri SourceUrl, string? Gtin, string? ArticleNumber);

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
    DateTimeOffset ObservedAt);

public enum CrawlStatus
{
    Succeeded,
    Failed
}

public sealed record CrawlResult(
    CrawlStatus Status,
    IReadOnlyList<ProductMatchCandidate> ProductMatches,
    IReadOnlyList<PriceObservationCandidate> PriceObservations);

public interface IWebsiteCrawler
{
    string Key { get; }

    Task<CrawlResult> CrawlAsync(CrawlTarget target, CancellationToken cancellationToken);
}
