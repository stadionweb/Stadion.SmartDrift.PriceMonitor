using Stadion.SmartDrift.PriceMonitor.Core;
using Stadion.SmartDrift.PriceMonitor.Crawler;

namespace Stadion.SmartDrift.PriceMonitor.Tests;

public class PersistedMonitoringTests
{
    private static readonly Uri Url = new("https://www.platousport.com/produkt/arcteryx-beta-sl-jacket-mens-black-2/");
    private static readonly MonitoringWorkItem Work = new(2, 2, 1, "platou-sport",
        new CrawlTarget(35148, "ArcTeryx", "Beta SL Jacket M", "X000010552",
            [new CrawlVariant(54618, "0623555782018", null, "M", null)]));
    private static readonly MonitoringWorkItem XlWork = Work with
    {
        Target = Work.Target with { Variants = [new CrawlVariant(54620, "0623555782032", null, "XL", null)] }
    };

    private static CrawlResult Result(CrawlTarget? target = null, string? html = null) =>
        PlatouSportProductParser.Parse(html ??
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PlatouSport", "captured-product-excerpt.html"))
                .Replace("5999", "6123"), Url, target ?? Work.Target,
            new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task RealIdentitiesAndParsedFactsReachPersistenceAndRerunUsesPolling()
    {
        var monitoring = new TestMonitoring();
        var runs = new TestRuns();
        var crawler = new TestCrawler(Result());
        var runner = new PersistedMonitoringRunner(monitoring, runs, new CrawlerRegistry([crawler]));
        var first = await runner.RunAsync(new(2, "0623555782018"), CancellationToken.None);
        monitoring.Match = new(7, "392833", Url, [new(54618, "392835", "0623555782018")]);
        var second = await runner.RunAsync(new(2, "0623555782018"), CancellationToken.None);
        Assert.Equal(0, first.ExitCode);
        Assert.Equal("Succeeded", second.Status);
        Assert.Equal(1, crawler.DiscoveryCalls);
        Assert.Equal(1, crawler.PollCalls);
        Assert.Equal(2, runs.Completed.Count);
        Assert.Equal(2, monitoring.PersistCalls);
        Assert.Equal(1, first.ProductMatchesCreated);
        Assert.Equal(1, second.ProductMatchesReused);
        Assert.Equal(1, second.VariantMatchesReused);
        Assert.Equal(1, first.ObservationsWritten);
        Assert.Equal(35148, monitoring.SavedWork!.Target.ProductId);
        var match = Assert.Single(monitoring.SavedResult!.VariantMatches);
        Assert.Equal(54618, match.VariantId);
        Assert.Equal("392835", match.ExternalVariationId);
        var observation = Assert.Single(monitoring.SavedResult.PriceObservations);
        Assert.Equal(6123m, observation.CurrentPrice);
        Assert.Equal("NOK", observation.CurrencyCode);
        Assert.True(observation.InStock);
        Assert.Equal(Url, observation.SourceUrl);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero), observation.ObservedAt);
        Assert.All(runs.Completed, s => { Assert.Equal(1, s.Requested); Assert.Equal(1, s.Succeeded); Assert.Equal(0, s.Failed); });
    }

    [Fact]
    public async Task VerifiedProductQualifiesAndReusesNewVariantWithoutDiscovery()
    {
        var monitoring = new TestMonitoring
        {
            WorkItem = XlWork,
            Match = new(7, "392833", Url, [new(54618, "392835", "0623555782018")])
        };
        var crawler = new TestCrawler(Result(XlWork.Target));
        var runs = new TestRuns();
        var runner = new PersistedMonitoringRunner(monitoring, runs, new CrawlerRegistry([crawler]));

        var first = await runner.RunAsync(new(2, "0623555782032"), CancellationToken.None);
        var firstMatch = Assert.Single(monitoring.SavedResult!.VariantMatches);
        Assert.Equal("Found", Assert.Single(first.Outcomes).Code);
        Assert.Equal(1, first.ProductMatchesReused);
        Assert.Equal(1, first.VariantMatchesCreated);
        Assert.Equal(1, first.ObservationsWritten);
        Assert.Equal("0623555782032", firstMatch.Gtin);
        Assert.Equal("392837", firstMatch.ExternalVariationId);
        Assert.Equal("xl", firstMatch.Size);
        var firstObservation = Assert.Single(monitoring.SavedResult.PriceObservations);
        Assert.Equal(6123m, firstObservation.CurrentPrice);
        Assert.Equal("NOK", firstObservation.CurrencyCode);
        Assert.True(firstObservation.InStock);
        Assert.Equal(Url, firstObservation.SourceUrl);

        var second = await runner.RunAsync(new(2, "0623555782032"), CancellationToken.None);
        Assert.Equal("Succeeded", second.Status);
        Assert.Equal(1, second.ProductMatchesReused);
        Assert.Equal(1, second.VariantMatchesReused);
        Assert.Equal(1, second.ObservationsWritten);
        Assert.Equal(0, crawler.DiscoveryCalls);
        Assert.Equal(2, crawler.PollCalls);
        Assert.Equal(2, monitoring.PersistCalls);
        Assert.Equal(2, runs.Completed.Count);
    }

    [Fact]
    public async Task MissingVariantAndAmbiguousGtinNeverPersistUnderVerifiedProduct()
    {
        var baseHtml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PlatouSport", "captured-product-excerpt.html"));
        foreach (var (html, expected) in new[]
        {
            (baseHtml.Replace("0623555782032", "0623555782017"), "NoMatch"),
            (baseHtml.Replace("0623555782018", "0623555782032"), "Ambiguous")
        })
        {
            var monitoring = new TestMonitoring
            {
                WorkItem = XlWork,
                Match = new(7, "392833", Url, [new(54618, "392835", "0623555782018")])
            };
            var runs = new TestRuns();
            var crawler = new TestCrawler(Result(XlWork.Target, html));
            var summary = await new PersistedMonitoringRunner(monitoring, runs, new CrawlerRegistry([crawler]))
                .RunAsync(new(2, "0623555782032"), CancellationToken.None);

            Assert.Equal(expected, Assert.Single(summary.Outcomes).Code);
            Assert.Equal("SucceededWithWarnings", summary.Status);
            Assert.Equal(0, monitoring.PersistCalls);
            Assert.Null(monitoring.SavedResult);
            Assert.Equal(0, summary.ObservationsWritten);
        }
    }

    [Theory]
    [InlineData(CrawlStatus.NotFound, "NoMatch", "SucceededWithWarnings")]
    [InlineData(CrawlStatus.Ambiguous, "Ambiguous", "SucceededWithWarnings")]
    [InlineData(CrawlStatus.Incomplete, "Incomplete", "SucceededWithWarnings")]
    [InlineData(CrawlStatus.Failed, "Failed", "Failed")]
    public async Task NonSuccessfulDiscoveryWritesOnlyOperationalOutcome(CrawlStatus status, string code, string runStatus)
    {
        var monitoring = new TestMonitoring();
        var runs = new TestRuns();
        var runner = new PersistedMonitoringRunner(monitoring, runs,
            new CrawlerRegistry([new TestCrawler(Result() with { Status = status, Diagnostic = "fixture diagnostic" })]));
        var summary = await runner.RunAsync(new(2, null), CancellationToken.None);
        Assert.Equal(0, monitoring.PersistCalls);
        Assert.Equal(code, Assert.Single(summary.Outcomes).Code);
        Assert.Equal(runStatus, summary.Status);
        Assert.Equal(code, Assert.Single(runs.Messages).Code);
        Assert.Equal(1, summary.ExitCode);
    }

    [Fact]
    public async Task UnknownWebsiteIsDiagnosableWithoutCrawling()
    {
        var monitoring = new TestMonitoring { WorkItem = Work with { WebsiteKey = "unregistered" } };
        var runs = new TestRuns();
        var runner = new PersistedMonitoringRunner(monitoring, runs, new CrawlerRegistry([]));
        var summary = await runner.RunAsync(new(2, null), CancellationToken.None);
        var outcome = Assert.Single(runs.Messages);
        Assert.Equal(2, outcome.TargetId);
        Assert.Equal("UnknownWebsiteAdapter", outcome.Code);
        Assert.Contains("unregistered", outcome.Diagnostic);
        Assert.Equal("Failed", summary.Status);
        Assert.Equal(0, monitoring.PersistCalls);
    }

    [Fact]
    public async Task BrokenMatchNeverFallsBackToDiscoveryOrWritesObservation()
    {
        var monitoring = new TestMonitoring { Match = new(7, "392833", Url, [new(54618, "392835", "0623555782018")]) };
        var crawler = new TestCrawler(new(CrawlStatus.Failed, [], []) { Diagnostic = "RequiresRequalification" });
        var runs = new TestRuns();
        var summary = await new PersistedMonitoringRunner(monitoring, runs, new CrawlerRegistry([crawler]))
            .RunAsync(new(2, null), CancellationToken.None);
        Assert.Equal("RequiresRequalification", Assert.Single(summary.Outcomes).Code);
        Assert.Equal(0, crawler.DiscoveryCalls);
        Assert.Equal(1, crawler.PollCalls);
        Assert.Equal(0, monitoring.PersistCalls);
        Assert.NotNull(monitoring.Match);
    }

    [Fact]
    public async Task PersistenceFailureHasExplicitOutcomeAndCompletedRun()
    {
        var monitoring = new TestMonitoring { ThrowOnPersist = true };
        var runs = new TestRuns();
        var summary = await new PersistedMonitoringRunner(monitoring, runs, new CrawlerRegistry([new TestCrawler(Result())]))
            .RunAsync(new(2, null), CancellationToken.None);
        Assert.Equal("Failed", summary.Status);
        Assert.Null(monitoring.SavedResult);
        Assert.Single(runs.Completed);
        Assert.Single(runs.Messages);
    }

    [Fact]
    public async Task CancellationDuringCrawlStopsPersistenceAndFinalizesRun()
    {
        using var cancellation = new CancellationTokenSource();
        var monitoring = new TestMonitoring();
        var runs = new TestRuns();
        var crawler = new TestCrawler(Result()) { DuringCrawl = () => cancellation.Cancel() };
        var summary = await new PersistedMonitoringRunner(monitoring, runs, new CrawlerRegistry([crawler]))
            .RunAsync(new(2, null), cancellation.Token);
        Assert.Equal("Cancelled", Assert.Single(summary.Outcomes).Code);
        Assert.Equal(0, monitoring.PersistCalls);
        Assert.Equal("Failed", Assert.Single(runs.Completed).Status);
        Assert.Equal(1, summary.ExitCode);
    }

    [Fact]
    public async Task MissingEligibleTargetFailsWithoutExternalWork()
    {
        var monitoring = new TestMonitoring { Eligible = false };
        var runs = new TestRuns();
        var crawler = new TestCrawler(Result());
        var summary = await new PersistedMonitoringRunner(monitoring, runs, new CrawlerRegistry([crawler]))
            .RunAsync(new(2, null), CancellationToken.None);
        Assert.Equal("NoEligibleTarget", Assert.Single(summary.Outcomes).Code);
        Assert.Equal(1, summary.ExitCode);
        Assert.Equal(0, crawler.DiscoveryCalls);
    }

    [Fact]
    public async Task CancellationWhileReportingStillWritesTerminalRunState()
    {
        using var cancellation = new CancellationTokenSource();
        var runs = new TestRuns { DuringMessage = () => cancellation.Cancel() };
        var runner = new PersistedMonitoringRunner(new TestMonitoring(), runs,
            new CrawlerRegistry([new TestCrawler(new(CrawlStatus.NotFound, [], []))]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(new(2, null), cancellation.Token));
        Assert.Equal("SucceededWithWarnings", Assert.Single(runs.Completed).Status);
    }

    [Fact]
    public void ValidationRejectsFakeOrCrossLinkedFactsBeforePersistence()
    {
        var result = Result();
        Assert.Throws<InvalidDataException>(() => MonitoringResultValidator.Validate(
            Work with { Target = Work.Target with { ProductId = 0 } }, result));
        Assert.Throws<InvalidDataException>(() => MonitoringResultValidator.Validate(Work,
            result with { VariantMatches = [result.VariantMatches[0] with { VariantId = 0 }] }));
        Assert.Throws<InvalidDataException>(() => MonitoringResultValidator.Validate(Work,
            result with { PriceObservations = [result.PriceObservations[0] with { SourceUrl = new("https://example.org/") }] }));
        Assert.Throws<InvalidDataException>(() => MonitoringResultValidator.Validate(Work,
            result with { PriceObservations = [result.PriceObservations[0] with { CurrentPrice = null }] }));
        Assert.Throws<InvalidDataException>(() => MonitoringResultValidator.Validate(Work,
            result with { PriceObservations = [result.PriceObservations[0] with { CurrencyCode = null }] }));
    }

    [Theory]
    [InlineData("--run persisted-monitoring --monitoring-target-id 2", true)]
    [InlineData("--run persisted-monitoring --monitoring-target-id 2 --gtin 0623555782018", true)]
    [InlineData("--run persisted-monitoring", false)]
    [InlineData("--run persisted-monitoring --monitoring-target-id 0", false)]
    [InlineData("--run persisted-monitoring --monitoring-target-id -1", false)]
    [InlineData("--run persisted-monitoring --monitoring-target-id 2 --monitoring-target-id 3", false)]
    [InlineData("--run persisted-monitoring --monitoring-target-id 2 --gtin invalid", false)]
    [InlineData("--run persisted-monitoring --monitoring-target-id 2 --offline-fixture relative.json", false)]
    public void CommandIsExplicitAndBounded(string command, bool valid)
    {
        Assert.Equal(valid, PersistedMonitoringCommand.TryParse(command.Split(' '), out _, out _));
    }

    private sealed class TestCrawler(CrawlResult result) : IPollingWebsiteCrawler
    {
        public string Key => "platou-sport";
        public int DiscoveryCalls { get; private set; }
        public int PollCalls { get; private set; }
        public Action? DuringCrawl { get; init; }
        public Task<CrawlResult> CrawlAsync(CrawlTarget target, CancellationToken cancellationToken)
        {
            DiscoveryCalls++;
            DuringCrawl?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
        public Task<CrawlResult> PollAsync(CrawlTarget target, VerifiedProductMatch match, CancellationToken cancellationToken)
        {
            PollCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class TestMonitoring : IMonitoringStore
    {
        public MonitoringWorkItem WorkItem { get; init; } = Work;
        public bool Eligible { get; init; } = true;
        public bool ThrowOnPersist { get; init; }
        public VerifiedProductMatch? Match { get; set; }
        public int PersistCalls { get; private set; }
        public MonitoringWorkItem? SavedWork { get; private set; }
        public CrawlResult? SavedResult { get; private set; }
        public Task<IReadOnlyList<MonitoringWorkItem>> ReadTargetsAsync(MonitoringSelection selection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(2, selection.TargetId);
            return Task.FromResult<IReadOnlyList<MonitoringWorkItem>>(Eligible ? [WorkItem] : []);
        }
        public Task<VerifiedProductMatch?> ReadVerifiedMatchAsync(MonitoringWorkItem work, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Match);
        }
        public Task<PersistedTargetCounts> PersistAsync(MonitoringWorkItem work, CrawlResult result, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnPersist) throw new IOException("fixture persistence fault");
            var existing = Match;
            var createdVariants = result.VariantMatches.Count(v =>
                existing is null || existing.Variants.All(m => m.VariantId != v.VariantId));
            PersistCalls++;
            SavedWork = work;
            SavedResult = result;
            if (existing is not null)
                Match = existing with
                {
                    Variants = existing.Variants.Concat(result.VariantMatches.Select(v =>
                        new VerifiedVariantMatch(v.VariantId, v.ExternalVariationId, v.Gtin))).ToArray()
                };
            return Task.FromResult(new PersistedTargetCounts(existing is null ? 1 : 0, existing is null ? 0 : 1,
                createdVariants, result.VariantMatches.Count - createdVariants, result.PriceObservations.Count));
        }
    }

    private sealed class TestRuns : IIntegrationRunStore
    {
        private long nextId;
        public List<MonitoringRunSummary> Completed { get; } = [];
        public List<MonitoringTargetOutcome> Messages { get; } = [];
        public Action? DuringMessage { get; init; }
        public Task<long> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(++nextId);
        }
        public Task MessageAsync(long runId, MonitoringTargetOutcome outcome, CancellationToken cancellationToken)
        {
            DuringMessage?.Invoke();
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
