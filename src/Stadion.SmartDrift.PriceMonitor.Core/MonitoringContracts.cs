namespace Stadion.SmartDrift.PriceMonitor.Core;

public sealed record MonitoringSelection(long TargetId, string? Gtin);

public sealed record MonitoringBatchSelection(string? WebsiteKey, int? MaxTargets)
{
    public const int HardMaxTargets = 100_000;
}

public sealed record MonitoringWorkItem(
    long TargetId, long SubscriptionId, int WebsiteId, string WebsiteKey, CrawlTarget Target);

public sealed record VerifiedVariantMatch(long VariantId, string? ExternalVariantId, string? Gtin);

public sealed record VerifiedProductMatch(
    long Id, string? ExternalProductId, Uri ProductUrl, IReadOnlyList<VerifiedVariantMatch> Variants);

public sealed record PersistedTargetCounts(
    int ProductMatchesCreated, int ProductMatchesReused,
    int VariantMatchesCreated, int VariantMatchesReused, int ObservationsWritten);

public sealed record MonitoringTargetOutcome(
    long TargetId, string Code, string? Diagnostic, PersistedTargetCounts? Persistence = null)
{
    public string? WebsiteKey { get; init; }
    public long? ProductId { get; init; }
    public long? ProductVariantId { get; init; }
    public string? Gtin { get; init; }
}

public sealed record MonitoringRunSummary(long RunId, int Requested, IReadOnlyList<MonitoringTargetOutcome> Outcomes)
{
    public string? WebsiteKey { get; init; }
    public int? MaxTargets { get; init; }
    public int? EligibleTargetCount { get; init; }
    public bool IsBatch { get; init; }
    public bool WasCancelled { get; init; }
    public bool FatalFailure { get; init; }
    public int EligibleTargets => EligibleTargetCount ?? Requested;
    public int Processed => Outcomes.Count;
    public int Succeeded => Outcomes.Count(o => o.Code == "Found");
    public int Skipped => Outcomes.Count(o => o.Code is "NoMatch" or "Ambiguous" or "Incomplete");
    public int Failed => Outcomes.Count - Succeeded - Skipped;
    public int Warnings => Skipped;
    public int Found => Succeeded;
    public int NoMatch => Outcomes.Count(o => o.Code == "NoMatch");
    public int Incomplete => Outcomes.Count(o => o.Code == "Incomplete");
    public int Ambiguous => Outcomes.Count(o => o.Code == "Ambiguous");
    public int RequiresRequalification => Outcomes.Count(o => o.Code == "RequiresRequalification");
    public int ObservationsWritten => Outcomes.Sum(o => o.Persistence?.ObservationsWritten ?? 0);
    public int ProductMatchesCreated => Outcomes.Sum(o => o.Persistence?.ProductMatchesCreated ?? 0);
    public int ProductMatchesReused => Outcomes.Sum(o => o.Persistence?.ProductMatchesReused ?? 0);
    public int VariantMatchesCreated => Outcomes.Sum(o => o.Persistence?.VariantMatchesCreated ?? 0);
    public int VariantMatchesReused => Outcomes.Sum(o => o.Persistence?.VariantMatchesReused ?? 0);
    public string Status => Failed > 0 || WasCancelled || FatalFailure
        ? "Failed" : Warnings > 0 ? "SucceededWithWarnings" : "Succeeded";
    public int ExitCode => IsBatch
        ? Failed == 0 && !WasCancelled && !FatalFailure ? 0 : 1
        : Succeeded == Requested && Requested > 0 && Failed == 0 && Warnings == 0 ? 0 : 1;
}

public interface IMonitoringStore
{
    Task<IReadOnlyList<MonitoringWorkItem>> ReadTargetsAsync(MonitoringSelection selection, CancellationToken cancellationToken);
    Task<VerifiedProductMatch?> ReadVerifiedMatchAsync(MonitoringWorkItem work, CancellationToken cancellationToken);
    Task<PersistedTargetCounts> PersistAsync(MonitoringWorkItem work, CrawlResult result, CancellationToken cancellationToken);
}

public interface IBatchedMonitoringStore
{
    Task<IReadOnlyList<MonitoringWorkItem>> ReadBatchTargetsAsync(
        MonitoringBatchSelection selection, CancellationToken cancellationToken);
}

public interface IIntegrationRunStore
{
    Task<long> StartAsync(CancellationToken cancellationToken);
    Task MessageAsync(long runId, MonitoringTargetOutcome outcome, CancellationToken cancellationToken);
    Task CompleteAsync(MonitoringRunSummary summary, CancellationToken cancellationToken);
}
