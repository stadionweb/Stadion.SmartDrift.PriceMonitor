using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Stadion.SmartDrift.PriceMonitor.Core;
using Stadion.SmartDrift.PriceMonitor.Infrastructure;

namespace Stadion.SmartDrift.PriceMonitor.Tests;

public sealed class SqlPersistenceTests
{
    private static readonly Uri ProductUrl = new("https://www.platousport.com/produkt/product/");
    private static readonly DateTimeOffset ObservedAt = new(2026, 11, 8, 13, 12, 11, TimeSpan.FromHours(2));
    private static MonitoringWorkItem Work => new(2, 2, 1, "platou-sport",
        new CrawlTarget(35148, "ArcTeryx", "Beta SL Jacket M", "X000010552",
            [new CrawlVariant(54618, "0623555782018", null, "M", null)]));
    private static CrawlResult Result => new(CrawlStatus.Succeeded,
        [new ProductMatchCandidate("Product'; DROP TABLE dbo.Products;--", ProductUrl, "0623555782018", null)
            { ExternalProductId = "392833", ExternalSku = "81551-31548" }],
        [new PriceObservationCandidate(6123.45m, 5432.10m, null, null, "NOK", true, "Available", null,
            ProductUrl, ObservedAt) { VariantId = 54618 }])
        { VariantMatches = [new ProductVariantMatchCandidate(54618, ProductUrl, "0623555782018", "M", null,
            VariantMatchMethod.ExactGtin) { ExternalVariationId = "392835" }] };

    [Fact]
    public async Task ReadTargetsUsesOnlyBoundedActiveIdentitiesAndExactParameterizedGtin()
    {
        var db = new ScriptedConnection();
        db.Responses.Enqueue(Table(
            [typeof(long), typeof(long), typeof(int), typeof(string), typeof(long), typeof(string), typeof(string),
                typeof(string), typeof(long), typeof(string), typeof(string), typeof(string)],
            [2L, 2L, 1, "platou-sport", 35148L, "ArcTeryx", "Beta SL Jacket M", "X000010552",
                54618L, "0623555782018", "M", DBNull.Value]));
        using var tokenSource = new CancellationTokenSource();
        var work = Assert.Single(await new SqlMonitoringStore(() => db)
            .ReadTargetsAsync(new MonitoringSelection(2, "0623555782018"), tokenSource.Token));
        Assert.Equal(35148, work.Target.ProductId);
        Assert.Equal(54618, Assert.Single(work.Target.Variants).Id);
        Assert.Equal("M", work.Target.Variants[0].Size);
        var command = Assert.Single(db.Commands);
        foreach (var eligibility in new[] { "t.Id = @TargetId", "t.Status = N'Active'", "s.Status = N'Active'",
            "w.IsActive = 1", "p.SourceActive = 1", "v.SourceActive = 1", "s.ValidFrom <=", "s.ValidTo >",
            "COALESCE(NULLIF(v.SizeInfo, N''), v.SizeName)", "v.Gtin COLLATE Latin1_General_100_BIN2 = @Gtin" })
            Assert.Contains(eligibility, command.CommandText);
        Assert.DoesNotContain("IsDefaultForMonitoring", command.CommandText);
        Assert.DoesNotContain("0623555782018", command.CommandText);
        Parameter(command, "@TargetId", 2L, DbType.Int64);
        Parameter(command, "@Gtin", "0623555782018", DbType.String, 20);
        Assert.All(db.Tokens, t => Assert.Equal(tokenSource.Token, t));
        Assert.Null(command.Transaction);
    }

    [Fact]
    public async Task NonpositiveTargetRejectedBeforeOpeningSql()
    {
        var opened = false;
        var store = new SqlMonitoringStore(() => { opened = true; return new ScriptedConnection(); });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ReadTargetsAsync(new MonitoringSelection(0, null), default));
        Assert.False(opened);
    }

    [Fact]
    public async Task OverlengthGtinCannotBeSilentlyTruncatedToAnotherSelection()
    {
        var opened = false;
        var store = new SqlMonitoringStore(() => { opened = true; return new ScriptedConnection(); });
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ReadTargetsAsync(new MonitoringSelection(2, new string('1', 21)), default));
        Assert.False(opened);
    }

    [Fact]
    public async Task NoExistingMatchReturnsNullAndVerifiedMatchUsesPersistedIdentity()
    {
        var empty = new ScriptedConnection();
        empty.Responses.Enqueue(MatchTable());
        Assert.Null(await new SqlMonitoringStore(() => empty).ReadVerifiedMatchAsync(Work, default));
        var db = new ScriptedConnection();
        db.Responses.Enqueue(MatchTable("Matched", "ExactGtin", ObservedAt.UtcDateTime, ProductUrl.AbsoluteUri));
        var match = await new SqlMonitoringStore(() => db).ReadVerifiedMatchAsync(Work, default);
        Assert.NotNull(match);
        Assert.Equal(100L, match.Id);
        Assert.Equal("392833", match.ExternalProductId);
        Assert.Equal(ProductUrl, match.ProductUrl);
        var variant = Assert.Single(match.Variants);
        Assert.Equal(54618, variant.VariantId);
        Assert.Equal("392835", variant.ExternalVariantId);
        Assert.Equal("0623555782018", variant.Gtin);
        Parameter(db.Commands[0], "@ProductId", 35148L, DbType.Int64);
        Parameter(db.Commands[0], "@WebsiteId", 1, DbType.Int32);
    }

    [Fact]
    public async Task VerifiedProductMatchIsReturnedWhenSelectedVariantHasNotBeenMatched()
    {
        var db = new ScriptedConnection();
        db.Responses.Enqueue(MatchTable("Matched", "ExactGtin", ObservedAt.UtcDateTime, ProductUrl.AbsoluteUri));
        var xlWork = Work with
        {
            Target = Work.Target with { Variants = [new CrawlVariant(54620, "0623555782032", null, "XL", null)] }
        };

        var match = await new SqlMonitoringStore(() => db).ReadVerifiedMatchAsync(xlWork, default);

        Assert.NotNull(match);
        Assert.Equal("392833", match.ExternalProductId);
        Assert.Empty(match.Variants);
    }

    [Theory]
    [InlineData("Candidate", "ExactGtin", true, "https://example.test/product")]
    [InlineData("Matched", "Fuzzy", true, "https://example.test/product")]
    [InlineData("Matched", "ExactGtin", false, "https://example.test/product")]
    [InlineData("Matched", "ExactGtin", true, "not a URL")]
    [InlineData("Matched", "ExactGtin", true, "http://example.test/product")]
    [InlineData("Matched", "ExactGtin", true, "******example.test/product")]
    public async Task ExistingUnusableMatchNeverBecomesDiscoveryNull(string status, string method, bool verified, string url)
    {
        var db = new ScriptedConnection();
        db.Responses.Enqueue(MatchTable(status, method, verified ? ObservedAt.UtcDateTime : DBNull.Value, url));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SqlMonitoringStore(() => db).ReadVerifiedMatchAsync(Work, default));
        Assert.Contains("qualification", error.Message);
    }

    [Theory]
    [InlineData(CrawlStatus.NotFound)]
    [InlineData(CrawlStatus.Ambiguous)]
    [InlineData(CrawlStatus.Failed)]
    [InlineData(CrawlStatus.Incomplete)]
    public async Task NonSuccessfulResultsNeverOpenSql(CrawlStatus status)
    {
        var opened = false;
        var store = new SqlMonitoringStore(() => { opened = true; return new ScriptedConnection(); });
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PersistAsync(Work, Result with { Status = status }, default));
        Assert.False(opened);
    }

    [Fact]
    public async Task ExactVariantValidationRunsBeforeSql()
    {
        var opened = false;
        var store = new SqlMonitoringStore(() => { opened = true; return new ScriptedConnection(); });
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PersistAsync(Work,
            Result with { VariantMatches = [Result.VariantMatches[0] with { Gtin = "wrong" }] }, default));
        Assert.False(opened);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProductionUpsertsReuseReturnedIdentitiesAndAppendTypedLiveObservation(bool created)
    {
        var db = PersistenceConnection(created);
        using var source = new CancellationTokenSource();
        var counts = await new SqlMonitoringStore(() => db).PersistAsync(Work, Result, source.Token);
        Assert.Equal(new PersistedTargetCounts(created ? 1 : 0, created ? 0 : 1,
            created ? 1 : 0, created ? 0 : 1, 1), counts);
        Assert.True(db.Transaction!.Committed);
        Assert.Equal(IsolationLevel.Serializable, db.Transaction.IsolationLevel);
        Assert.False(db.Transaction.RolledBack);
        Assert.All(db.Commands, c => Assert.Same(db.Transaction, c.Transaction));
        Assert.All(db.Tokens, t => Assert.Equal(source.Token, t));
        var eligibility = db.Commands[0];
        Assert.Contains("s.Id = @SubscriptionId", eligibility.CommandText);
        Assert.Contains("v.Id = @VariantId", eligibility.CommandText);
        Assert.Contains("DATALENGTH(v.Gtin) = DATALENGTH(@Gtin)", eligibility.CommandText);
        Parameter(eligibility, "@Gtin", "0623555782018", DbType.String, 20);
        Parameter(eligibility, "@WebsiteKey", "platou-sport", DbType.String, 100);
        var product = db.Commands[1];
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", product.CommandText);
        Assert.Contains("WHERE ProductId = @ProductId AND WebsiteId = @WebsiteId", product.CommandText);
        Assert.Contains("THROW 51000", product.CommandText);
        Assert.DoesNotContain("FirstMatchedAt =", product.CommandText);
        Assert.DoesNotContain("DROP TABLE", product.CommandText);
        Assert.DoesNotContain("ExternalSku", product.CommandText);
        Assert.DoesNotContain("DELETE", product.CommandText);
        Parameter(product, "@ExternalName", Result.ProductMatches[0].Name, DbType.String, 500);
        Parameter(product, "@ExternalProductId", "392833", DbType.String, 200);
        Parameter(product, "@ProductUrl", ProductUrl.AbsoluteUri, DbType.String, 2000);
        Parameter(product, "@VerifiedAt", ObservedAt.UtcDateTime, DbType.DateTime2);
        var variant = db.Commands[2];
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", variant.CommandText);
        Assert.Contains("ProductMatchId = @ProductMatchId AND ProductVariantId = @VariantId", variant.CommandText);
        Assert.Contains("THROW 51001", variant.CommandText);
        Assert.DoesNotContain("DELETE", variant.CommandText);
        Parameter(variant, "@ProductMatchId", 100L, DbType.Int64);
        Parameter(variant, "@VariantId", 54618L, DbType.Int64);
        Parameter(variant, "@ExternalVariantId", "392835", DbType.String, 200);
        var observation = db.Commands[3];
        Assert.StartsWith("INSERT dbo.PriceObservations", observation.CommandText);
        Assert.DoesNotContain("UPDATE", observation.CommandText);
        Assert.DoesNotContain("DELETE", observation.CommandText);
        Parameter(observation, "@ProductMatchId", 100L, DbType.Int64);
        Parameter(observation, "@VariantMatchId", 200L, DbType.Int64);
        Parameter(observation, "@ObservedAt", ObservedAt.UtcDateTime, DbType.DateTime2);
        Parameter(observation, "@CurrentPrice", 5432.10m, DbType.Decimal);
        Assert.Equal((byte)18, observation.Parameters["@CurrentPrice"].Precision);
        Assert.Equal((byte)2, observation.Parameters["@CurrentPrice"].Scale);
        Parameter(observation, "@CurrencyCode", "NOK", DbType.AnsiStringFixedLength, 3);
        Parameter(observation, "@InStock", true, DbType.Boolean);
        Parameter(observation, "@SourceUrl", ProductUrl.AbsoluteUri, DbType.String, 2000);
    }

    [Fact]
    public async Task MultipleExactVariantsAndObservationsShareOneProductTransaction()
    {
        var work = Work with { Target = Work.Target with { Variants =
            [.. Work.Target.Variants, new CrawlVariant(54620, "0623555782032", null, "XL", null)] } };
        var result = Result with
        {
            VariantMatches = [.. Result.VariantMatches, Result.VariantMatches[0] with
                { VariantId = 54620, Gtin = "0623555782032", Size = "XL", ExternalVariationId = "392837" }],
            PriceObservations = [.. Result.PriceObservations, Result.PriceObservations[0] with { VariantId = 54620 }]
        };
        var db = new ScriptedConnection();
        foreach (var response in new object[] { 1L, 1L, Identity(100, true), Identity(200, true),
            Identity(201, true), 1, 1 }) db.Responses.Enqueue(response);
        var counts = await new SqlMonitoringStore(() => db).PersistAsync(work, result, default);
        Assert.Equal(new PersistedTargetCounts(1, 0, 2, 0, 2), counts);
        Assert.All(db.Commands, command => Assert.Same(db.Transaction, command.Transaction));
        Parameter(db.Commands[^1], "@VariantMatchId", 201L, DbType.Int64);
        Assert.True(db.Transaction!.Committed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InjectedProductVariantOrObservationFailureRollsBack(int failureIndex)
    {
        var db = PersistenceConnection(true);
        db.FailureIndex = failureIndex;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqlMonitoringStore(() => db).PersistAsync(Work, Result, default));
        Assert.True(db.Transaction!.RolledBack);
        Assert.False(db.Transaction.Committed);
        Assert.All(db.Commands, command => Assert.Same(db.Transaction, command.Transaction));
    }

    [Theory]
    [InlineData(51000, 1)]
    [InlineData(51001, 2)]
    public async Task SqlIdentityConflictRollsBackAndBecomesRequalification(int errorNumber, int commandIndex)
    {
        var db = PersistenceConnection(false);
        db.FailureIndex = commandIndex;
        db.InjectedException = SqlFailure(errorNumber);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SqlMonitoringStore(() => db).PersistAsync(Work, Result, default));
        Assert.Contains("qualification", error.Message);
        Assert.Same(db.InjectedException, error.InnerException);
        Assert.True(db.Transaction!.RolledBack);
        Assert.False(db.Transaction.Committed);
        Assert.DoesNotContain(db.Commands, command => command.CommandText.StartsWith("INSERT dbo.PriceObservations"));
    }

    [Fact]
    public async Task UnexpectedSqlConstraintFailureRollsBackWithoutMisclassifyingIdentity()
    {
        var db = PersistenceConnection(false);
        db.FailureIndex = 1;
        db.InjectedException = SqlFailure(2601);
        var error = await Assert.ThrowsAsync<SqlException>(() =>
            new SqlMonitoringStore(() => db).PersistAsync(Work, Result, default));
        Assert.Same(db.InjectedException, error);
        Assert.True(db.Transaction!.RolledBack);
        Assert.False(db.Transaction.Committed);
    }

    [Fact]
    public async Task EligibilityChangedBeforePersistenceRollsBackBeforeUpserts()
    {
        var db = new ScriptedConnection();
        db.Responses.Enqueue(0L);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqlMonitoringStore(() => db).PersistAsync(Work, Result, default));
        Assert.Single(db.Commands);
        Assert.True(db.Transaction!.RolledBack);
    }

    [Fact]
    public async Task CancellationDuringObservationRollsBackWithCleanupToken()
    {
        using var source = new CancellationTokenSource();
        var db = PersistenceConnection(true);
        db.BeforeExecute = index => { if (index == 3) source.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SqlMonitoringStore(() => db).PersistAsync(Work, Result, source.Token));
        Assert.True(db.Transaction!.RolledBack);
        Assert.False(db.Transaction.Committed);
        Assert.Equal(CancellationToken.None, db.Transaction.RollbackToken);
    }

    [Fact]
    public async Task IntegrationStartRequiresPreconfiguredActiveKeyAndRunningManualRun()
    {
        var db = new ScriptedConnection();
        db.Responses.Enqueue(99L);
        Assert.Equal(99L, await new SqlIntegrationRunStore(() => db).StartAsync(default));
        var command = Assert.Single(db.Commands);
        Assert.Contains("THROW 51002", command.CommandText);
        Assert.Contains("AND IsActive = 1", command.CommandText);
        Assert.Contains("N'Manual', N'Running'", command.CommandText);
        Assert.DoesNotContain("INSERT dbo.Integrations", command.CommandText);
        Parameter(command, "@Key", "price-monitor-observation", DbType.String, 100);
    }

    [Fact]
    public async Task MissingIntegrationFailureIsNotSilentlyProvisioned()
    {
        var db = new ScriptedConnection { FailureIndex = 0 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqlIntegrationRunStore(() => db).StartAsync(default));
        Assert.Single(db.Commands);
        Assert.DoesNotContain("INSERT dbo.Integrations", db.Commands[0].CommandText);
    }

    [Theory]
    [InlineData("Found", "Succeeded", 1, 0, 0)]
    [InlineData("Incomplete", "SucceededWithWarnings", 0, 1, 0)]
    [InlineData("Failed", "Failed", 0, 0, 1)]
    public async Task IntegrationCompletionPersistsCanonicalCountsAndOneSummary(string code, string status,
        int succeeded, int skipped, int failed)
    {
        var db = new ScriptedConnection();
        db.Responses.Enqueue(1);
        db.Responses.Enqueue(1);
        var summary = new MonitoringRunSummary(99, 1,
            [new MonitoringTargetOutcome(2, code, "diagnostic", new PersistedTargetCounts(1, 0, 1, 0, 1))]);
        await new SqlIntegrationRunStore(() => db).CompleteAsync(summary, default);
        var command = db.Commands[0];
        Parameter(command, "@Status", status, DbType.String, 30);
        Parameter(command, "@Discovered", 1, DbType.Int32);
        Parameter(command, "@Processed", 1, DbType.Int32);
        Parameter(command, "@Succeeded", succeeded, DbType.Int32);
        Parameter(command, "@Skipped", skipped, DbType.Int32);
        Parameter(command, "@Failed", failed, DbType.Int32);
        Parameter(command, "@Warnings", skipped, DbType.Int32);
        Assert.Contains("CompletedAt = SYSUTCDATETIME()", command.CommandText);
        Assert.Contains("Status = N'Running'", command.CommandText);
        Assert.All(db.Commands, c => Assert.Same(db.Transaction, c.Transaction));
        Assert.True(db.Transaction!.Committed);
        var details = Assert.IsType<string>(db.Commands[1].Parameters["@DetailsJson"].Value);
        Assert.Contains("\"ObservationsWritten\":1", details);
        Assert.Contains("\"ProductMatchesCreated\":1", details);
        Assert.DoesNotContain("ObservationsWritten", command.CommandText);
    }

    [Fact]
    public async Task IntegrationFailureMessageIsBoundedParameterizedAndSuccessIsQuiet()
    {
        var db = new ScriptedConnection();
        db.Responses.Enqueue(1);
        var store = new SqlIntegrationRunStore(() => db);
        await store.MessageAsync(99, new MonitoringTargetOutcome(2, "Found", null), default);
        Assert.Empty(db.Commands);
        await store.MessageAsync(99, new MonitoringTargetOutcome(2, "Failed", new string('x', 2100)), default);
        var command = Assert.Single(db.Commands);
        Parameter(command, "@Severity", "Error", DbType.String, 20);
        Parameter(command, "@EntityType", "MonitoringTarget", DbType.String, 100);
        Parameter(command, "@EntityId", "2", DbType.String, 200);
        Assert.Equal(2000, Assert.IsType<string>(command.Parameters["@Message"].Value).Length);
    }

    [Fact]
    public async Task IntegrationSummaryFailureRollsBackCompletionAndForwardsToken()
    {
        var db = new ScriptedConnection { FailureIndex = 1 };
        db.Responses.Enqueue(1);
        using var source = new CancellationTokenSource();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqlIntegrationRunStore(() => db)
            .CompleteAsync(new MonitoringRunSummary(99, 1, [new MonitoringTargetOutcome(2, "Failed", "failure")]), source.Token));
        Assert.True(db.Transaction!.RolledBack);
        Assert.False(db.Transaction.Committed);
        Assert.All(db.Tokens, token => Assert.Equal(source.Token, token));
    }

    private static ScriptedConnection PersistenceConnection(bool created)
    {
        var db = new ScriptedConnection();
        foreach (var response in new object[] { 1L, Identity(100, created), Identity(200, created), 1 })
            db.Responses.Enqueue(response);
        return db;
    }

    private static SqlException SqlFailure(int number)
    {
        // SqlClient exposes no public error constructor; build an actual provider exception
        // to exercise the production Number filter, not a repository-shaped test exception.
        var constructor = typeof(SqlError).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .OrderBy(c => c.GetParameters().Length).First();
        var arguments = constructor.GetParameters().Select(p =>
            p.ParameterType == typeof(int) ? (object)(p.Name == "infoNumber" ? number : 0) :
            p.ParameterType == typeof(byte) ? (byte)0 :
            p.ParameterType == typeof(uint) ? 0u :
            p.ParameterType == typeof(string) ? "Injected identity conflict" : null).ToArray();
        var error = (SqlError)constructor.Invoke(arguments);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(errors, [error]);
        var factory = typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(m => m.Name == "CreateException" && m.GetParameters()[0].ParameterType == typeof(SqlErrorCollection))
            .OrderBy(m => m.GetParameters().Length).First();
        var factoryArguments = factory.GetParameters().Select(p =>
            p.ParameterType == typeof(SqlErrorCollection) ? (object)errors :
            p.ParameterType == typeof(string) ? "16.0.0" :
            p.ParameterType == typeof(Guid) ? Guid.Empty : null).ToArray();
        var exception = (SqlException)factory.Invoke(null, factoryArguments)!;
        Assert.Equal(number, exception.Number);
        return exception;
    }

    private static DataTable Identity(long id, bool created) => Table([typeof(long), typeof(bool)], [id, created]);
    private static DataTable MatchTable(string? status = null, string? method = null, object? verified = null, string? url = null) =>
        Table([typeof(long), typeof(string), typeof(string), typeof(string), typeof(string), typeof(DateTime),
            typeof(long), typeof(string), typeof(string), typeof(string), typeof(string)],
            status is null ? null : [100L, "392833", url!, status, method!, verified!,
                54618L, "392835", "0623555782018", "Matched", "ExactGtin"]);

    private static DataTable Table(Type[] types, object[]? row)
    {
        var table = new DataTable();
        for (var i = 0; i < types.Length; i++) table.Columns.Add($"C{i}", types[i]);
        if (row is not null) table.Rows.Add(row);
        return table;
    }

    private static void Parameter(DbCommand command, string name, object value, DbType type, int? size = null)
    {
        var parameter = command.Parameters[name];
        Assert.Equal(value, parameter.Value);
        Assert.Equal(type, parameter.DbType);
        if (size is not null) Assert.Equal(size, parameter.Size);
    }

    private sealed class ScriptedConnection : DbConnection
    {
        private ConnectionState state;
        public Queue<object> Responses { get; } = new();
        public List<DbCommand> Commands { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public RecordingTransaction? Transaction { get; private set; }
        public int FailureIndex { get; set; } = -1;
        public Exception? InjectedException { get; set; }
        public Action<int>? BeforeExecute { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "Test";
        public override string DataSource => "Test";
        public override string ServerVersion => "Test";
        public override ConnectionState State => state;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => state = ConnectionState.Closed;
        public override void Open() => state = ConnectionState.Open;
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Open();
            return Task.CompletedTask;
        }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            Transaction = new RecordingTransaction(this, isolationLevel);
        protected override ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(BeginDbTransaction(isolationLevel));
        }
        protected override DbCommand CreateDbCommand() => new RecordingCommand(this);
        public object Execute(DbCommand command, CancellationToken token)
        {
            var index = Commands.Count;
            Commands.Add(command);
            Tokens.Add(token);
            BeforeExecute?.Invoke(index);
            token.ThrowIfCancellationRequested();
            if (index == FailureIndex) throw InjectedException ?? new InvalidOperationException("Injected SQL command failure.");
            return Responses.Dequeue();
        }
    }

    private sealed class RecordingTransaction(ScriptedConnection connection, IsolationLevel isolationLevel) : DbTransaction
    {
        public bool Committed { get; private set; }
        public bool RolledBack { get; private set; }
        public CancellationToken RollbackToken { get; private set; }
        public override IsolationLevel IsolationLevel => isolationLevel;
        protected override DbConnection DbConnection => connection;
        public override void Commit() => Committed = true;
        public override void Rollback() => RolledBack = true;
        public override Task CommitAsync(CancellationToken cancellationToken = default)
        {
            connection.Tokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Commit();
            return Task.CompletedTask;
        }
        public override Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackToken = cancellationToken;
            Rollback();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCommand(ScriptedConnection connection) : DbCommand
    {
        private readonly SqlCommand parameterHost = new();
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameterHost.Parameters;
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new SqlParameter();
        public override int ExecuteNonQuery() => Convert.ToInt32(connection.Execute(this, default));
        public override object ExecuteScalar() => connection.Execute(this, default);
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            ((DataTable)connection.Execute(this, default)).CreateDataReader();
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Convert.ToInt32(connection.Execute(this, cancellationToken)));
        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
            Task.FromResult<object?>(connection.Execute(this, cancellationToken));
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
            Task.FromResult<DbDataReader>(((DataTable)connection.Execute(this, cancellationToken)).CreateDataReader());
    }
}
