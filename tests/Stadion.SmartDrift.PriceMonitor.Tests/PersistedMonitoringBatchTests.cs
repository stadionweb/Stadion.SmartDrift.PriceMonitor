using Stadion.SmartDrift.PriceMonitor.Core;
using Stadion.SmartDrift.PriceMonitor.Crawler;

namespace Stadion.SmartDrift.PriceMonitor.Tests;

public sealed class PersistedMonitoringBatchTests
{
    [Theory]
    [InlineData("--run persisted-monitoring-batch", true)]
    [InlineData("--run persisted-monitoring-batch --website-key platou-sport --max-targets 10", true)]
    [InlineData("--run persisted-monitoring-batch --max-targets 0", false)]
    [InlineData("--run persisted-monitoring-batch --max-targets 100001", false)]
    [InlineData("--run persisted-monitoring-batch --max-targets 5 --max-targets 6", false)]
    [InlineData("--run persisted-monitoring-batch --offline-fixture relative.json", false)]
    [InlineData("--run persisted-monitoring-batch --unknown value", false)]
    public void BatchCommandIsExplicitAndBounded(string command, bool valid) =>
        Assert.Equal(valid, PersistedMonitoringBatchCommand.TryParse(command.Split(' '), out _, out _));

    [Fact]
    public async Task BatchProcessesSequentiallyAndContinuesAfterTargetFailure()
    {
        var items = new[] { Work(1, 101), Work(2, 102), Work(3, 103) };
        var crawler = new BatchCrawler(async (target, token) =>
        {
            await Task.Delay(5, token);
            return Result(target.ProductId == 102 ? CrawlStatus.Failed : CrawlStatus.NotFound);
        });
        var runs = new BatchRuns();
        var summary = await Runner(items, crawler, runs).RunBatchAsync(new(null, 3), CancellationToken.None);

        Assert.Equal(new long[] { 101, 102, 103 }, crawler.Products);
        Assert.Equal(1, crawler.MaximumConcurrency);
        Assert.Equal(new[] { "NoMatch", "Failed", "NoMatch" }, summary.Outcomes.Select(o => o.Code));
        Assert.Equal(3, summary.EligibleTargets);
        Assert.Equal(3, summary.Processed);
        Assert.Equal(2, summary.Skipped);
        Assert.Equal(1, summary.Failed);
        Assert.Equal("Failed", summary.Status);
        Assert.Equal(1, runs.StartCount);
        Assert.Single(runs.Completed);
        Assert.Equal(3, runs.Messages.Count);
    }

    [Fact]
    public async Task CancellationStopsStartingFurtherTargetsAndFinalizesOneRun()
    {
        using var cancellation = new CancellationTokenSource();
        var items = new[] { Work(1, 101), Work(2, 102) };
        var crawler = new BatchCrawler((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Result(CrawlStatus.NotFound));
        });
        var runs = new BatchRuns();

        var summary = await Runner(items, crawler, runs).RunBatchAsync(new(null, null), cancellation.Token);

        Assert.Equal(new long[] { 101 }, crawler.Products);
        Assert.Equal("Cancelled", Assert.Single(summary.Outcomes).Code);
        Assert.True(summary.WasCancelled);
        Assert.Equal(1, summary.Processed);
        Assert.Equal("Failed", summary.Status);
        Assert.Single(runs.Completed);
    }

    [Fact]
    public async Task BatchUsesMediumVariantForDiscoveryAndOnlyQualifiedVariantsForPolling()
    {
        var variants = new[]
        {
            new CrawlVariant(2, "0000000000002", null, "L", null),
            new CrawlVariant(3, "0000000000003", null, "S", null),
            new CrawlVariant(8, "0000000000008", null, "M", null)
        };
        var work = Work(1, 101) with { Target = Work(1, 101).Target with { Variants = variants } };
        var discovery = new BatchCrawler((_, _) => Task.FromResult(Result(CrawlStatus.Incomplete)));
        await Runner([work], discovery, new BatchRuns()).RunBatchAsync(new(null, 1), CancellationToken.None);
        Assert.Equal(new long[] { 8 }, discovery.TargetVariantIds);

        var match = new VerifiedProductMatch(7, "product", new("https://www.platousport.com/product"),
            [new(2, "external-2", variants[0].Gtin), new(3, "external-3", variants[1].Gtin)]);
        var monitoring = new BatchMonitoring([work]) { Match = match };
        var polling = new BatchCrawler((_, _) => Task.FromResult(Result(CrawlStatus.Incomplete)));
        await new PersistedMonitoringRunner(monitoring, new BatchRuns(), new CrawlerRegistry([polling]))
            .RunBatchAsync(new(null, 1), CancellationToken.None);
        Assert.Equal(new long[] { 2, 3 }, polling.PolledVariantIds);
    }

    private static PersistedMonitoringRunner Runner(
        IReadOnlyList<MonitoringWorkItem> items, BatchCrawler crawler, BatchRuns runs) =>
        new(new BatchMonitoring(items), runs, new CrawlerRegistry([crawler]));

    private static MonitoringWorkItem Work(long targetId, long productId) =>
        new(targetId, targetId, 1, "platou-sport",
            new(productId, "Brand", "Product", null,
                [new CrawlVariant(productId, "0623555782018", null, "M", null)]));

    private static CrawlResult Result(CrawlStatus status) => new(status, [], []);

    private sealed class BatchMonitoring(IReadOnlyList<MonitoringWorkItem> work) :
        IMonitoringStore, IBatchedMonitoringStore
    {
        public VerifiedProductMatch? Match { get; init; }

        public Task<IReadOnlyList<MonitoringWorkItem>> ReadBatchTargetsAsync(
            MonitoringBatchSelection selection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<MonitoringWorkItem>>(
                work.Take(selection.MaxTargets ?? MonitoringBatchSelection.HardMaxTargets).ToArray());
        }

        public Task<IReadOnlyList<MonitoringWorkItem>> ReadTargetsAsync(
            MonitoringSelection selection, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MonitoringWorkItem>>([]);

        public Task<VerifiedProductMatch?> ReadVerifiedMatchAsync(
            MonitoringWorkItem item, CancellationToken cancellationToken) =>
            Task.FromResult(Match);

        public Task<PersistedTargetCounts> PersistAsync(
            MonitoringWorkItem item, CrawlResult result, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class BatchCrawler(
        Func<CrawlTarget, CancellationToken, Task<CrawlResult>> crawl) : IPollingWebsiteCrawler
    {
        private int active;
        public string Key => "platou-sport";
        public int MaximumConcurrency { get; private set; }
        public List<long> Products { get; } = [];
        public List<long> TargetVariantIds { get; } = [];
        public List<long> PolledVariantIds { get; } = [];

        public async Task<CrawlResult> CrawlAsync(CrawlTarget target, CancellationToken cancellationToken)
        {
            Products.Add(target.ProductId);
            TargetVariantIds.AddRange(target.Variants.Select(v => v.Id));
            MaximumConcurrency = Math.Max(MaximumConcurrency, Interlocked.Increment(ref active));
            try { return await crawl(target, cancellationToken); }
            finally { Interlocked.Decrement(ref active); }
        }

        public async Task<CrawlResult> PollAsync(
            CrawlTarget target, VerifiedProductMatch match, CancellationToken cancellationToken)
        {
            PolledVariantIds.AddRange(target.Variants.Select(v => v.Id));
            return await crawl(target, cancellationToken);
        }
    }

    private sealed class BatchRuns : IIntegrationRunStore
    {
        private long nextId;
        public int StartCount { get; private set; }
        public List<MonitoringRunSummary> Completed { get; } = [];
        public List<MonitoringTargetOutcome> Messages { get; } = [];

        public Task<long> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            return Task.FromResult(++nextId);
        }

        public Task MessageAsync(long runId, MonitoringTargetOutcome outcome, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add(outcome);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(MonitoringRunSummary summary, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completed.Add(summary);
            return Task.CompletedTask;
        }
    }
}
