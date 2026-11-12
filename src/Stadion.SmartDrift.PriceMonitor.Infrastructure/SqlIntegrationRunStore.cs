using System.Data;
using System.Data.Common;
using System.Text.Json;
using Stadion.SmartDrift.PriceMonitor.Core;

namespace Stadion.SmartDrift.PriceMonitor.Infrastructure;

public sealed class SqlIntegrationRunStore : IIntegrationRunStore
{
    private readonly Func<DbConnection> connectionFactory;

    public SqlIntegrationRunStore(string connectionString) : this(SqlStore.Factory(connectionString)) { }

    public SqlIntegrationRunStore(Func<DbConnection> connectionFactory) =>
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<long> StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory();
        await connection.OpenAsync(cancellationToken);
        await using var command = SqlStore.Command(connection, """
            DECLARE @IntegrationId int;
            IF (SELECT COUNT(*) FROM dbo.Integrations WHERE [Key] = @Key AND IsActive = 1) <> 1
                THROW 51002, 'Exactly one active price-monitor-observation integration must be registered manually.', 1;
            SELECT @IntegrationId = Id FROM dbo.Integrations WHERE [Key] = @Key AND IsActive = 1;
            INSERT dbo.IntegrationRuns
              (IntegrationId, TriggerType, Status, StartedAt, HeartbeatAt)
            VALUES (@IntegrationId, N'Manual', N'Running', SYSUTCDATETIME(), SYSUTCDATETIME());
            SELECT CONVERT(bigint, SCOPE_IDENTITY());
            """);
        SqlStore.Add(command, "@Key", DbType.String, "price-monitor-observation", 100);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task MessageAsync(long runId, MonitoringTargetOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runId);
        if (outcome.Code == "Found") return;
        await using var connection = connectionFactory();
        await connection.OpenAsync(cancellationToken);
        var details = JsonSerializer.Serialize(new
        {
            outcome.WebsiteKey,
            outcome.ProductId,
            outcome.ProductVariantId,
            outcome.Gtin,
            ResultCode = outcome.Code
        });
        await using var command = MessageCommand(connection, runId,
            outcome.Code is "NoMatch" or "Ambiguous" or "Incomplete" ? "Warning" : "Error",
            outcome.Code, outcome.Diagnostic ?? outcome.Code, "MonitoringTarget",
            outcome.TargetId.ToString(System.Globalization.CultureInfo.InvariantCulture), details);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteAsync(MonitoringRunSummary summary, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(summary.RunId);
        await using var connection = connectionFactory();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = SqlStore.Command(connection, """
                UPDATE dbo.IntegrationRuns SET Status = @Status, CompletedAt = SYSUTCDATETIME(),
                    HeartbeatAt = SYSUTCDATETIME(), ItemsDiscovered = @Discovered, ItemsProcessed = @Processed,
                    ItemsSucceeded = @Succeeded, ItemsSkipped = @Skipped, ItemsFailed = @Failed,
                    WarningCount = @Warnings, ErrorCode = @ErrorCode, ErrorSummary = @ErrorSummary
                WHERE Id = @RunId AND Status = N'Running';
                IF @@ROWCOUNT <> 1 THROW 51003, 'Integration run is missing or already completed.', 1;
                """, transaction);
            SqlStore.Add(command, "@RunId", DbType.Int64, summary.RunId);
            SqlStore.Add(command, "@Status", DbType.String, summary.Status, 30);
            SqlStore.Add(command, "@Discovered", DbType.Int32, summary.Requested);
            SqlStore.Add(command, "@Processed", DbType.Int32, summary.Outcomes.Count);
            SqlStore.Add(command, "@Succeeded", DbType.Int32, summary.Succeeded);
            SqlStore.Add(command, "@Skipped", DbType.Int32, summary.Skipped);
            SqlStore.Add(command, "@Failed", DbType.Int32, summary.Failed);
            SqlStore.Add(command, "@Warnings", DbType.Int32, summary.Warnings);
            var failure = summary.Outcomes.FirstOrDefault(o => o.Code is not ("Found" or "NoMatch" or "Ambiguous" or "Incomplete"));
            var errorCode = failure?.Code ?? (summary.WasCancelled ? "Cancelled" :
                summary.FatalFailure ? "BatchSelectionFailed" : null);
            var errorSummary = failure?.Diagnostic ?? (summary.WasCancelled ? "Batch execution was cancelled." :
                summary.FatalFailure ? "Batch work selection or orchestration failed." : null);
            SqlStore.Add(command, "@ErrorCode", DbType.String, SqlStore.Bound(errorCode, 100), 100);
            SqlStore.Add(command, "@ErrorSummary", DbType.String, SqlStore.Bound(errorSummary, 2000), 2000);
            await command.ExecuteNonQueryAsync(cancellationToken);
            var details = JsonSerializer.Serialize(new
            {
                summary.IsBatch,
                summary.WebsiteKey,
                summary.MaxTargets,
                summary.EligibleTargets,
                summary.Processed,
                summary.Found,
                summary.NoMatch,
                summary.Incomplete,
                summary.Ambiguous,
                summary.RequiresRequalification,
                summary.WasCancelled,
                summary.FatalFailure,
                summary.ProductMatchesCreated, summary.ProductMatchesReused,
                summary.VariantMatchesCreated, summary.VariantMatchesReused, summary.ObservationsWritten
            });
            await using var message = MessageCommand(connection, summary.RunId, "Information", "RunSummary",
                "Monitoring run completed.", null, null, details, transaction);
            await message.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static DbCommand MessageCommand(DbConnection connection, long runId, string severity, string code,
        string message, string? entityType, string? entityId, string? details, DbTransaction? transaction = null)
    {
        var command = SqlStore.Command(connection, """
            INSERT dbo.IntegrationRunMessages
              (IntegrationRunId, Severity, Code, Message, EntityType, EntityId, DetailsJson)
            VALUES (@RunId, @Severity, @Code, @Message, @EntityType, @EntityId, @DetailsJson);
            """, transaction);
        SqlStore.Add(command, "@RunId", DbType.Int64, runId);
        SqlStore.Add(command, "@Severity", DbType.String, severity, 20);
        SqlStore.Add(command, "@Code", DbType.String, SqlStore.Bound(code, 100), 100);
        SqlStore.Add(command, "@Message", DbType.String, SqlStore.Bound(message, 2000), 2000);
        SqlStore.Add(command, "@EntityType", DbType.String, entityType, 100);
        SqlStore.Add(command, "@EntityId", DbType.String, entityId, 200);
        SqlStore.Add(command, "@DetailsJson", DbType.String, details, -1);
        return command;
    }
}
