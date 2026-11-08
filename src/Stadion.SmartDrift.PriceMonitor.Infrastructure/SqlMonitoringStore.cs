using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Stadion.SmartDrift.PriceMonitor.Core;

namespace Stadion.SmartDrift.PriceMonitor.Infrastructure;

public sealed class SqlMonitoringStore : IMonitoringStore
{
    private readonly Func<DbConnection> connectionFactory;

    public SqlMonitoringStore(string connectionString) : this(SqlStore.Factory(connectionString)) { }

    public SqlMonitoringStore(Func<DbConnection> connectionFactory) =>
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    private const string EligibleFrom = """
        FROM dbo.MonitoringTargets t
        JOIN dbo.MonitoringSubscriptions s ON s.Id = t.MonitoringSubscriptionId
        JOIN dbo.Websites w ON w.Id = t.WebsiteId
        JOIN dbo.Products p ON p.Id = s.ProductId
        JOIN dbo.ProductVariants v ON v.ProductId = p.Id
        WHERE t.Id = @TargetId AND t.Status = N'Active' AND s.Status = N'Active'
          AND w.IsActive = 1 AND p.SourceActive = 1 AND v.SourceActive = 1
          AND s.ValidFrom <= SYSUTCDATETIME()
          AND (s.ValidTo IS NULL OR s.ValidTo > SYSUTCDATETIME())
        """;

    public async Task<IReadOnlyList<MonitoringWorkItem>> ReadTargetsAsync(
        MonitoringSelection selection, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(selection.TargetId);
        if (selection.Gtin?.Length > 20)
            throw new ArgumentException("GTIN exceeds the database contract length.", nameof(selection));
        await using var connection = connectionFactory();
        await connection.OpenAsync(cancellationToken);
        await using var command = SqlStore.Command(connection, """
            SELECT t.Id, s.Id, w.Id, w.[Key], p.Id, p.Brand, p.Name, p.ProducerArticleNumber,
                   v.Id, v.Gtin, COALESCE(NULLIF(v.SizeInfo, N''), v.SizeName), v.ColorName
            """ + "\n" + EligibleFrom + """

              AND (@Gtin IS NULL OR
                  (v.Gtin COLLATE Latin1_General_100_BIN2 = @Gtin COLLATE Latin1_General_100_BIN2
                   AND DATALENGTH(v.Gtin) = DATALENGTH(@Gtin)))
            ORDER BY v.Id;
            """);
        SqlStore.Add(command, "@TargetId", DbType.Int64, selection.TargetId);
        SqlStore.Add(command, "@Gtin", DbType.String, selection.Gtin, 20);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var variants = new List<CrawlVariant>();
        MonitoringWorkItem? work = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            work ??= new MonitoringWorkItem(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2),
                reader.GetString(3), new CrawlTarget(reader.GetInt64(4), SqlStore.Text(reader, 5),
                    SqlStore.Text(reader, 6), SqlStore.Text(reader, 7), variants));
            variants.Add(new CrawlVariant(reader.GetInt64(8), SqlStore.Text(reader, 9), null,
                SqlStore.Text(reader, 10), SqlStore.Text(reader, 11)));
        }
        return work is null ? [] : [work];
    }

    public async Task<VerifiedProductMatch?> ReadVerifiedMatchAsync(
        MonitoringWorkItem work, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory();
        await connection.OpenAsync(cancellationToken);
        await using var command = SqlStore.Command(connection, """
            SELECT m.Id, m.ExternalProductId, m.ProductUrl, m.MatchStatus, m.MatchMethod, m.LastVerifiedAt,
                   v.ProductVariantId, v.ExternalVariantId, v.ExternalGtin, v.MatchStatus, v.MatchMethod
            FROM dbo.ProductMatches m
            LEFT JOIN dbo.ProductVariantMatches v ON v.ProductMatchId = m.Id
            WHERE m.ProductId = @ProductId AND m.WebsiteId = @WebsiteId;
            """);
        SqlStore.Add(command, "@ProductId", DbType.Int64, work.Target.ProductId);
        SqlStore.Add(command, "@WebsiteId", DbType.Int32, work.WebsiteId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        VerifiedProductMatch? match = null;
        var variants = new List<VerifiedVariantMatch>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(SqlStore.Text(reader, 1)) ||
                reader.GetString(3) != "Matched" || reader.GetString(4) != "ExactGtin" ||
                reader.IsDBNull(5) || !Uri.TryCreate(SqlStore.Text(reader, 2), UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo) || url.AbsoluteUri.Length > 2000)
                throw new InvalidDataException("Existing product match requires qualification; automatic reassignment is prohibited.");
            match ??= new VerifiedProductMatch(reader.GetInt64(0), SqlStore.Text(reader, 1), url, variants);
            if (reader.IsDBNull(6)) continue;
            var expected = work.Target.Variants.SingleOrDefault(v => v.Id == reader.GetInt64(6));
            if (expected is null) continue;
            if (SqlStore.Text(reader, 9) != "Matched" || SqlStore.Text(reader, 10) != "ExactGtin" ||
                string.IsNullOrWhiteSpace(expected.Gtin) ||
                !string.Equals(expected.Gtin, SqlStore.Text(reader, 8), StringComparison.Ordinal))
                throw new InvalidDataException("Existing variant match requires qualification; automatic reassignment is prohibited.");
            variants.Add(new VerifiedVariantMatch(expected.Id, SqlStore.Text(reader, 7), SqlStore.Text(reader, 8)));
        }
        if (match is not null && work.Target.Variants.Any(v => variants.All(m => m.VariantId != v.Id)))
            throw new InvalidDataException("Existing product match lacks a verified selected variant and requires qualification.");
        return match;
    }

    public async Task<PersistedTargetCounts> PersistAsync(
        MonitoringWorkItem work, CrawlResult result, CancellationToken cancellationToken)
    {
        MonitoringResultValidator.Validate(work, result);
        await using var connection = connectionFactory();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            foreach (var variant in result.VariantMatches)
            {
                await using var eligible = SqlStore.Command(connection, "SELECT COUNT_BIG(*)\n" + EligibleFrom + """

                      AND s.Id = @SubscriptionId AND p.Id = @ProductId AND w.Id = @WebsiteId
                      AND w.[Key] COLLATE Latin1_General_100_BIN2 = @WebsiteKey COLLATE Latin1_General_100_BIN2
                      AND DATALENGTH(w.[Key]) = DATALENGTH(@WebsiteKey)
                      AND v.Id = @VariantId
                      AND v.Gtin COLLATE Latin1_General_100_BIN2 = @Gtin COLLATE Latin1_General_100_BIN2
                      AND DATALENGTH(v.Gtin) = DATALENGTH(@Gtin);
                    """, transaction);
                SqlStore.Add(eligible, "@TargetId", DbType.Int64, work.TargetId);
                SqlStore.Add(eligible, "@SubscriptionId", DbType.Int64, work.SubscriptionId);
                SqlStore.Add(eligible, "@ProductId", DbType.Int64, work.Target.ProductId);
                SqlStore.Add(eligible, "@WebsiteId", DbType.Int32, work.WebsiteId);
                SqlStore.Add(eligible, "@WebsiteKey", DbType.String, work.WebsiteKey, 100);
                SqlStore.Add(eligible, "@VariantId", DbType.Int64, variant.VariantId);
                SqlStore.Add(eligible, "@Gtin", DbType.String, variant.Gtin, 20);
                if (Convert.ToInt64(await eligible.ExecuteScalarAsync(cancellationToken)) != 1)
                    throw new InvalidOperationException("Monitoring target or source variant is no longer eligible.");
            }

            var candidate = result.ProductMatches.Single();
            var verifiedAt = result.PriceObservations.Max(o => o.ObservedAt).UtcDateTime;
            await using var product = SqlStore.Command(connection, """
                DECLARE @Id bigint, @Created bit = 0;
                SELECT @Id = Id FROM dbo.ProductMatches WITH (UPDLOCK, HOLDLOCK)
                WHERE ProductId = @ProductId AND WebsiteId = @WebsiteId;
                IF @Id IS NULL
                BEGIN
                    INSERT dbo.ProductMatches
                      (ProductId, WebsiteId, ExternalProductId, ExternalName, ProductUrl, MatchStatus,
                       MatchMethod, FirstMatchedAt, LastVerifiedAt)
                    VALUES (@ProductId, @WebsiteId, @ExternalProductId, @ExternalName, @ProductUrl,
                            N'Matched', N'ExactGtin', @VerifiedAt, @VerifiedAt);
                    SET @Id = CONVERT(bigint, SCOPE_IDENTITY());
                    SET @Created = 1;
                END
                ELSE
                BEGIN
                    IF EXISTS (SELECT 1 FROM dbo.ProductMatches WHERE Id = @Id AND
                        ((ExternalProductId IS NOT NULL AND
                          (@ExternalProductId IS NULL OR ExternalProductId COLLATE Latin1_General_100_BIN2 <> @ExternalProductId COLLATE Latin1_General_100_BIN2
                           OR DATALENGTH(ExternalProductId) <> DATALENGTH(@ExternalProductId)))
                         OR ProductUrl COLLATE Latin1_General_100_BIN2 <> @ProductUrl COLLATE Latin1_General_100_BIN2
                         OR DATALENGTH(ProductUrl) <> DATALENGTH(@ProductUrl)
                         OR MatchStatus <> N'Matched' OR MatchMethod <> N'ExactGtin' OR LastVerifiedAt IS NULL))
                        THROW 51000, 'Established product identity conflicts or requires qualification.', 1;
                    UPDATE dbo.ProductMatches SET ExternalProductId = @ExternalProductId,
                        ExternalName = @ExternalName, LastVerifiedAt = @VerifiedAt,
                        UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id;
                END;
                SELECT @Id, @Created;
                """, transaction);
            SqlStore.Add(product, "@ProductId", DbType.Int64, work.Target.ProductId);
            SqlStore.Add(product, "@WebsiteId", DbType.Int32, work.WebsiteId);
            SqlStore.Add(product, "@ExternalProductId", DbType.String, candidate.ExternalProductId, 200);
            SqlStore.Add(product, "@ExternalName", DbType.String, candidate.Name, 500);
            SqlStore.Add(product, "@ProductUrl", DbType.String, candidate.SourceUrl.AbsoluteUri, 2000);
            SqlStore.Add(product, "@VerifiedAt", DbType.DateTime2, verifiedAt);
            var (productId, productCreated) = await ReadIdentityAsync(product, cancellationToken);
            var variantIds = new Dictionary<long, long>();
            var variantCreated = 0;
            foreach (var variant in result.VariantMatches.OrderBy(v => v.VariantId))
            {
                await using var command = SqlStore.Command(connection, """
                    DECLARE @Id bigint, @Created bit = 0;
                    SELECT @Id = Id FROM dbo.ProductVariantMatches WITH (UPDLOCK, HOLDLOCK)
                    WHERE ProductMatchId = @ProductMatchId AND ProductVariantId = @VariantId;
                    IF @Id IS NULL
                    BEGIN
                        INSERT dbo.ProductVariantMatches
                          (ProductMatchId, ProductVariantId, ExternalVariantId, ExternalGtin, ExternalSize,
                           ExternalColor, VariantUrl, MatchStatus, MatchMethod)
                        VALUES (@ProductMatchId, @VariantId, @ExternalVariantId, @Gtin, @Size,
                                @Color, @Url, N'Matched', N'ExactGtin');
                        SET @Id = CONVERT(bigint, SCOPE_IDENTITY());
                        SET @Created = 1;
                    END
                    ELSE
                    BEGIN
                        IF EXISTS (SELECT 1 FROM dbo.ProductVariantMatches WHERE Id = @Id AND
                            ((ExternalVariantId IS NOT NULL AND
                              (@ExternalVariantId IS NULL OR ExternalVariantId COLLATE Latin1_General_100_BIN2 <> @ExternalVariantId COLLATE Latin1_General_100_BIN2
                               OR DATALENGTH(ExternalVariantId) <> DATALENGTH(@ExternalVariantId)))
                             OR ExternalGtin IS NULL OR ExternalGtin COLLATE Latin1_General_100_BIN2 <> @Gtin COLLATE Latin1_General_100_BIN2
                             OR DATALENGTH(ExternalGtin) <> DATALENGTH(@Gtin)
                             OR MatchStatus <> N'Matched' OR MatchMethod <> N'ExactGtin'))
                            THROW 51001, 'Established variant identity conflicts or requires qualification.', 1;
                        UPDATE dbo.ProductVariantMatches SET ExternalVariantId = @ExternalVariantId,
                            ExternalSize = @Size, ExternalColor = @Color, VariantUrl = @Url,
                            UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id;
                    END;
                    SELECT @Id, @Created;
                    """, transaction);
                SqlStore.Add(command, "@ProductMatchId", DbType.Int64, productId);
                SqlStore.Add(command, "@VariantId", DbType.Int64, variant.VariantId);
                SqlStore.Add(command, "@ExternalVariantId", DbType.String, variant.ExternalVariationId, 200);
                SqlStore.Add(command, "@Gtin", DbType.String, variant.Gtin, 20);
                SqlStore.Add(command, "@Size", DbType.String, variant.Size, 100);
                SqlStore.Add(command, "@Color", DbType.String, variant.Color, 200);
                SqlStore.Add(command, "@Url", DbType.String, variant.ProductUrl.AbsoluteUri, 2000);
                var (id, created) = await ReadIdentityAsync(command, cancellationToken);
                variantIds.Add(variant.VariantId, id);
                if (created) variantCreated++;
            }
            foreach (var observation in result.PriceObservations)
            {
                await using var command = SqlStore.Command(connection, """
                    INSERT dbo.PriceObservations
                      (ProductMatchId, ProductVariantMatchId, ObservedAt, RegularPrice, CurrentPrice,
                       PreviousPrice, MemberPrice, CurrencyCode, InStock, StockText, PromotionText, SourceUrl)
                    VALUES (@ProductMatchId, @VariantMatchId, @ObservedAt, @RegularPrice, @CurrentPrice,
                            @PreviousPrice, @MemberPrice, @CurrencyCode, @InStock, @StockText, @PromotionText, @SourceUrl);
                    """, transaction);
                SqlStore.Add(command, "@ProductMatchId", DbType.Int64, productId);
                SqlStore.Add(command, "@VariantMatchId", DbType.Int64, variantIds[observation.VariantId!.Value]);
                SqlStore.Add(command, "@ObservedAt", DbType.DateTime2, observation.ObservedAt.UtcDateTime);
                SqlStore.Add(command, "@RegularPrice", DbType.Decimal, observation.RegularPrice);
                SqlStore.Add(command, "@CurrentPrice", DbType.Decimal, observation.CurrentPrice);
                SqlStore.Add(command, "@PreviousPrice", DbType.Decimal, observation.PreviousPrice);
                SqlStore.Add(command, "@MemberPrice", DbType.Decimal, observation.MemberPrice);
                SqlStore.Add(command, "@CurrencyCode", DbType.AnsiStringFixedLength, observation.CurrencyCode, 3);
                SqlStore.Add(command, "@InStock", DbType.Boolean, observation.InStock);
                SqlStore.Add(command, "@StockText", DbType.String, observation.StockText, 500);
                SqlStore.Add(command, "@PromotionText", DbType.String, observation.PromotionText, 1000);
                SqlStore.Add(command, "@SourceUrl", DbType.String, observation.SourceUrl.AbsoluteUri, 2000);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new PersistedTargetCounts(productCreated ? 1 : 0, productCreated ? 0 : 1,
                variantCreated, result.VariantMatches.Count - variantCreated, result.PriceObservations.Count);
        }
        catch (SqlException exception) when (exception.Number is 51000 or 51001)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new InvalidDataException("Established external identity conflicts or requires qualification; automatic reassignment is prohibited.", exception);
        }
        catch
        {
            // Rollback must still run when the caller's token has been cancelled.
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<(long Id, bool Created)> ReadIdentityAsync(
        DbCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("SQL upsert did not return an identity.");
        return (reader.GetInt64(0), reader.GetBoolean(1));
    }
}
