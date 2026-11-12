using System.Net;
using System.Text.Json;
using Stadion.SmartDrift.PriceMonitor.Core;

namespace Stadion.SmartDrift.PriceMonitor.Crawler;

public sealed record OfflineMonitoringFixture(
    MonitoringWorkItem Work, Dictionary<string, string> Responses, VerifiedProductMatch? Match = null)
{
    public IReadOnlyList<MonitoringWorkItem>? BatchWorkItems { get; init; }
    public Dictionary<string, VerifiedProductMatch>? MatchesByTargetId { get; init; }

    public static async Task<PersistedMonitoringRunner> CreateRunnerAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var fixture = await JsonSerializer.DeserializeAsync<OfflineMonitoringFixture>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Empty offline fixture.");
        var crawler = new PlatouSportCrawler(new HttpClient(new FixtureHttp(fixture.Responses)),
            new PlatouSportOptions(RequestInterval: TimeSpan.Zero));
        return new(new FixtureMonitoring(fixture), new FixtureRuns(), new CrawlerRegistry([crawler]));
    }

    private sealed class FixtureHttp(Dictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var body);
            return Task.FromResult(new HttpResponseMessage(found ? HttpStatusCode.OK : HttpStatusCode.NotFound)
                { Content = new StringContent(body ?? "") });
        }
    }

    private sealed class FixtureMonitoring(OfflineMonitoringFixture fixture) : IMonitoringStore, IBatchedMonitoringStore
    {
        private bool persisted;

        public Task<IReadOnlyList<MonitoringWorkItem>> ReadTargetsAsync(MonitoringSelection selection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var variants = fixture.Work.Target.Variants.Where(v => selection.Gtin is null || v.Gtin == selection.Gtin).ToArray();
            IReadOnlyList<MonitoringWorkItem> work = fixture.Work.TargetId == selection.TargetId && variants.Length > 0
                ? [fixture.Work with { Target = fixture.Work.Target with { Variants = variants } }] : [];
            return Task.FromResult(work);
        }

        public Task<IReadOnlyList<MonitoringWorkItem>> ReadBatchTargetsAsync(
            MonitoringBatchSelection selection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targets = (fixture.BatchWorkItems ?? [fixture.Work])
                .Where(w => selection.WebsiteKey is null ||
                    string.Equals(w.WebsiteKey, selection.WebsiteKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(w => w.TargetId)
                .Take(selection.MaxTargets ?? MonitoringBatchSelection.HardMaxTargets)
                .ToArray();
            return Task.FromResult<IReadOnlyList<MonitoringWorkItem>>(targets);
        }

        public Task<VerifiedProductMatch?> ReadVerifiedMatchAsync(MonitoringWorkItem work, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(fixture.MatchesByTargetId?.GetValueOrDefault(work.TargetId.ToString()) ?? fixture.Match);
        }

        public Task<PersistedTargetCounts> PersistAsync(MonitoringWorkItem work, CrawlResult result, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MonitoringResultValidator.Validate(work, result);
            var reused = persisted || fixture.Match is not null;
            persisted = true;
            return Task.FromResult(new PersistedTargetCounts(reused ? 0 : 1, reused ? 1 : 0,
                reused ? 0 : result.VariantMatches.Count, reused ? result.VariantMatches.Count : 0, result.PriceObservations.Count));
        }
    }

    private sealed class FixtureRuns : IIntegrationRunStore
    {
        public Task<long> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(1L);
        }

        public Task MessageAsync(long runId, MonitoringTargetOutcome outcome, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task CompleteAsync(MonitoringRunSummary summary, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
