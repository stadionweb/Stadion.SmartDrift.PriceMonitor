using Stadion.SmartDrift.PriceMonitor.Core;

namespace Stadion.SmartDrift.PriceMonitor.Crawler;

public sealed class PersistedMonitoringRunner(
    IMonitoringStore monitoring, IIntegrationRunStore runs, CrawlerRegistry crawlers)
{
    public async Task<MonitoringRunSummary> RunBatchAsync(
        MonitoringBatchSelection selection, CancellationToken cancellationToken)
    {
        if (selection.WebsiteKey is { Length: > 100 } ||
            selection.MaxTargets is < 1 or > MonitoringBatchSelection.HardMaxTargets)
            throw new ArgumentException("A batch requires a valid Website.Key and positive bounded target limit.");

        var runId = await runs.StartAsync(cancellationToken);
        var outcomes = new List<MonitoringTargetOutcome>();
        var eligible = 0;
        var cancelled = false;
        var fatalFailure = false;
        try
        {
            if (monitoring is not IBatchedMonitoringStore batchStore)
                throw new InvalidOperationException("The monitoring store does not support batch selection.");
            var work = await batchStore.ReadBatchTargetsAsync(selection, cancellationToken);
            if (work.Count > selection.MaxTargets.GetValueOrDefault(MonitoringBatchSelection.HardMaxTargets) ||
                work.Zip(work.Skip(1), (previous, next) => previous.TargetId < next.TargetId).Any(ordered => !ordered))
                throw new InvalidDataException("The monitoring store exceeded the batch bound or returned unstable target ordering.");
            eligible = work.Count;
            foreach (var item in work)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                try
                {
                    var outcome = await ProcessAsync(item, cancellationToken, batchMode: true);
                    outcomes.Add(WithContext(outcome, item));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    outcomes.Add(WithContext(new(item.TargetId, "Cancelled",
                        "The batch was cancelled; no future targets will start."), item));
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception)
        {
            fatalFailure = true;
        }

        var summary = new MonitoringRunSummary(runId, eligible, outcomes)
        {
            IsBatch = true,
            WebsiteKey = selection.WebsiteKey,
            MaxTargets = selection.MaxTargets ?? MonitoringBatchSelection.HardMaxTargets,
            EligibleTargetCount = eligible,
            WasCancelled = cancelled,
            FatalFailure = fatalFailure
        };
        await FinalizeAsync(summary, cancellationToken);
        return summary;
    }

    public async Task<MonitoringRunSummary> RunAsync(MonitoringSelection selection, CancellationToken cancellationToken)
    {
        if (selection.TargetId <= 0 || selection.Gtin is not null &&
            (selection.Gtin.Length is < 8 or > 20 || !selection.Gtin.All(char.IsAsciiDigit)))
            throw new ArgumentException("A positive monitoring target ID and an optional numeric GTIN are required.");

        var runId = await runs.StartAsync(cancellationToken);
        var outcomes = new List<MonitoringTargetOutcome>();
        var requested = 0;
        try
        {
            var work = await monitoring.ReadTargetsAsync(selection, cancellationToken);
            if (work.Count > 1 || work.Any(w => w.TargetId != selection.TargetId))
                throw new InvalidDataException("The monitoring store returned work outside the one-target bound.");
            requested = work.Count;
            if (work.Count == 0)
                outcomes.Add(new(selection.TargetId, "NoEligibleTarget", "No active target/variant satisfies the explicit selection."));
            foreach (var item in work)
            {
                cancellationToken.ThrowIfCancellationRequested();
                outcomes.Add(await ProcessAsync(item, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcomes.Add(new(selection.TargetId, "Cancelled", "The one-shot run was cancelled; no future target work will start."));
        }
        catch (Exception)
        {
            outcomes.Add(new(selection.TargetId, "RunFailed", "Monitoring input or operational reporting failed."));
        }

        var summary = new MonitoringRunSummary(runId, requested, outcomes);
        // Finalize cancellation with a separate bounded token; the cancelled crawl token cannot write its terminal state.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completionToken = cancellationToken.IsCancellationRequested ? cleanup.Token : cancellationToken;
        try
        {
            foreach (var outcome in outcomes.Where(o => o.Code != "Found"))
                await runs.MessageAsync(runId, outcome, completionToken);
        }
        finally
        {
            try
            {
                await runs.CompleteAsync(summary, cancellationToken.IsCancellationRequested ? cleanup.Token : cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await runs.CompleteAsync(summary, cleanup.Token);
            }
        }
        return summary;
    }

    private async Task FinalizeAsync(MonitoringRunSummary summary, CancellationToken cancellationToken)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completionToken = cancellationToken.IsCancellationRequested ? cleanup.Token : cancellationToken;
        try
        {
            foreach (var outcome in summary.Outcomes.Where(o => o.Code != "Found"))
                await runs.MessageAsync(summary.RunId, outcome, completionToken);
        }
        finally
        {
            try
            {
                await runs.CompleteAsync(summary, cancellationToken.IsCancellationRequested ? cleanup.Token : cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await runs.CompleteAsync(summary, cleanup.Token);
            }
        }
    }

    private async Task<MonitoringTargetOutcome> ProcessAsync(
        MonitoringWorkItem work, CancellationToken cancellationToken, bool batchMode = false)
    {
        var crawler = crawlers.Find(work.WebsiteKey);
        if (crawler is null)
            return new(work.TargetId, "UnknownWebsiteAdapter", $"No registered adapter for Website.Key '{work.WebsiteKey}'.");
        try
        {
            var persisted = await monitoring.ReadVerifiedMatchAsync(work, cancellationToken);
            CrawlResult result;
            if (batchMode)
            {
                if (persisted is null)
                {
                    var representative = work.Target.Variants
                        .Where(v => v.Gtin is not null)
                        .OrderByDescending(v => string.Equals(v.Size, "M", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(v => v.Id)
                        .FirstOrDefault();
                    if (representative is null)
                        return new(work.TargetId, "Incomplete", "No active variant with a GTIN is available for bounded qualification.");
                    work = work with { Target = work.Target with { Variants = [representative] } };
                }
                else
                {
                    var qualified = work.Target.Variants
                        .Where(v => persisted.Variants.Any(match => match.VariantId == v.Id))
                        .OrderBy(v => v.Id)
                        .ToArray();
                    if (qualified.Length == 0)
                        return new(work.TargetId, "Incomplete", "No active variant has a verified match; automatic batch fan-out is disabled.");
                    work = work with { Target = work.Target with { Variants = qualified } };
                }
            }
            if (persisted is not null)
            {
                if (crawler is not IPollingWebsiteCrawler polling)
                    return new(work.TargetId, "RequiresRequalification", "Adapter cannot verify the persisted match.");
                result = await polling.PollAsync(work.Target, persisted, cancellationToken);
                if (result.Status == CrawlStatus.Failed)
                    return new(work.TargetId, "RequiresRequalification", result.Diagnostic ?? "Persisted identity could not be verified.");
            }
            else
            {
                result = await crawler.CrawlAsync(work.Target, cancellationToken);
            }

            if (result.Status != CrawlStatus.Succeeded)
                return new(work.TargetId, result.Status switch
                {
                    CrawlStatus.NotFound => "NoMatch",
                    CrawlStatus.Ambiguous => "Ambiguous",
                    CrawlStatus.Incomplete => "Incomplete",
                    _ => "Failed"
                }, result.Diagnostic);
            MonitoringResultValidator.Validate(work, result);
            var counts = await monitoring.PersistAsync(work, result, cancellationToken);
            return new(work.TargetId, "Found", null, counts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (InvalidDataException ex)
        {
            return new(work.TargetId, "RequiresRequalification", ex.Message);
        }
        catch (Exception)
        {
            return new(work.TargetId, "Failed", "Target crawl or persistence failed; previous committed data is preserved.");
        }
    }

    private static MonitoringTargetOutcome WithContext(MonitoringTargetOutcome outcome, MonitoringWorkItem work)
    {
        var variant = work.Target.Variants.Where(v => v.Gtin is not null)
            .OrderByDescending(v => string.Equals(v.Size, "M", StringComparison.OrdinalIgnoreCase))
            .ThenBy(v => v.Id)
            .FirstOrDefault();
        return outcome with
        {
            WebsiteKey = work.WebsiteKey,
            ProductId = work.Target.ProductId,
            ProductVariantId = variant?.Id,
            Gtin = variant?.Gtin
        };
    }
}
