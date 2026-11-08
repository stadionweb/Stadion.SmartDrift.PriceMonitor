using Stadion.SmartDrift.PriceMonitor.Core;

namespace Stadion.SmartDrift.PriceMonitor.Crawler;

public sealed class PersistedMonitoringRunner(
    IMonitoringStore monitoring, IIntegrationRunStore runs, CrawlerRegistry crawlers)
{
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

    private async Task<MonitoringTargetOutcome> ProcessAsync(MonitoringWorkItem work, CancellationToken cancellationToken)
    {
        var crawler = crawlers.Find(work.WebsiteKey);
        if (crawler is null)
            return new(work.TargetId, "UnknownWebsiteAdapter", $"No registered adapter for Website.Key '{work.WebsiteKey}'.");
        try
        {
            var persisted = await monitoring.ReadVerifiedMatchAsync(work, cancellationToken);
            CrawlResult result;
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
}
