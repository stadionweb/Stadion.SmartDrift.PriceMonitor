# Authoritative database contract

The environment schema is owned and managed **manually**. The Worker never creates tables,
executes migrations, provisions monitoring subscriptions/targets, or derives work from
`Websites.IsDefaultForMonitoring`. SQL Server persistence uses `Microsoft.Data.SqlClient`.
Infrastructure directly pins `System.Security.Cryptography.Pkcs` 10.0.14 to replace SqlClient's
vulnerable transitive 9.0.13 dependency; this is a security override, not a second persistence provider.
Connection strings belong in local configuration/user secrets/environment variables, not source
control or diagnostics. Use separate connections for `SmartDrift_PriceMonitor` and
`SmartDrift_Integration`.

Types and columns below reflect the supplied, validated environment contracts. Unless explicitly
marked nullable (`NULL`), columns are required. All `Id` primary keys are `IDENTITY`.

## SmartDrift_PriceMonitor.dbo

### Source and monitoring configuration (read-only for the Worker)

| Table | Columns |
| --- | --- |
| `Products` | `Id bigint PK`; available source identity/metadata fields: `SourceSystem`, `SourceItemId`, `Name`, `Brand`, `ProducerArticleNumber`, `SourceStatus`, `SourceActive`, `ImportedAt`, `UpdatedAt`. Types/nullability for these metadata fields were not supplied; the Worker does not alter them. |
| `ProductVariants` | `Id bigint PK`, `ProductId bigint`, `SourceItemSubId bigint`, `Gtin nvarchar(20) NULL`, `SizeId int NULL`, `SizeName nvarchar(100) NULL`, `SizeInfo nvarchar(200) NULL`, `ColorId int NULL`, `ColorName nvarchar(200) NULL`, `SourceActive bit`, `ImportedAt datetime2`, `UpdatedAt datetime2`. |
| `MonitoringSubscriptions` | `Id bigint PK`, `ProductId bigint`, `Status nvarchar(30) DEFAULT 'Active'`, `Priority nvarchar(20) DEFAULT 'Normal'`, `ValidFrom datetime2 DEFAULT sysutcdatetime()`, `ValidTo datetime2 NULL`, `CreatedBy nvarchar(200) NULL`, `CreatedAt datetime2 DEFAULT sysutcdatetime()`, `UpdatedAt datetime2 DEFAULT sysutcdatetime()`. |
| `MonitoringTargets` | `Id bigint PK`, `MonitoringSubscriptionId bigint`, `WebsiteId int`, `Status nvarchar(30) DEFAULT 'Active'`, `CreatedAt datetime2 DEFAULT sysutcdatetime()`, `UpdatedAt datetime2 DEFAULT sysutcdatetime()`. |
| `Websites` | `Id int PK`, `[Key] nvarchar(100)`, `Name nvarchar(200)`, `BaseUrl nvarchar(1000)`, `CountryCode char(2) DEFAULT 'NO'`, `CurrencyCode char(3) DEFAULT 'NOK'`, `IsActive bit DEFAULT 1`, `CreatedAt datetime2 DEFAULT sysutcdatetime()`, `UpdatedAt datetime2 DEFAULT sysutcdatetime()`, `IsDefaultForMonitoring bit DEFAULT 0`. |

Relationships/uniqueness:

- `ProductVariants.ProductId` → `Products.Id`; `UNIQUE(SourceItemSubId)`.
- `MonitoringSubscriptions.ProductId` → `Products.Id`; `UNIQUE(ProductId)`.
- `MonitoringTargets.MonitoringSubscriptionId` → `MonitoringSubscriptions.Id`;
  `MonitoringTargets.WebsiteId` → `Websites.Id`; `UNIQUE(MonitoringSubscriptionId, WebsiteId)`.
- `Websites`: `UNIQUE([Key])`. The key selects the crawler adapter; an unknown key is an explicit failure.

Selection **requires a positive explicit target ID**. Only the matching target is read, with
target/subscription status `Active`, active website, active source product and variants, and
`ValidFrom <= current UTC < ValidTo` (or no `ValidTo`). An optional GTIN filter is exact,
case-sensitive binary equality with equal byte length (so SQL's trailing-space padding cannot
broaden the selection), not fuzzy matching. Overlength GTIN inputs fail before SQL instead of
being truncated. Sizes use nonempty `SizeInfo`, falling back
to `SizeName`; `SizeName` need not be populated.

### Matches and append-only observations

| Table | Columns |
| --- | --- |
| `ProductMatches` | `Id bigint PK`, `ProductId bigint`, `WebsiteId int`, `ExternalProductId nvarchar(200) NULL`, `ExternalName nvarchar(500) NULL`, `ProductUrl nvarchar(2000)`, `MatchStatus nvarchar(30)`, `MatchMethod nvarchar(30)`, `MatchConfidence decimal(5,2) NULL`, `FirstMatchedAt datetime2 DEFAULT sysutcdatetime()`, `LastVerifiedAt datetime2 NULL`, `ConfirmedBy nvarchar(200) NULL`, `CreatedAt datetime2 DEFAULT sysutcdatetime()`, `UpdatedAt datetime2 DEFAULT sysutcdatetime()`. |
| `ProductVariantMatches` | `Id bigint PK`, `ProductMatchId bigint`, `ProductVariantId bigint`, `ExternalVariantId nvarchar(200) NULL`, `ExternalGtin nvarchar(20) NULL`, `ExternalSize nvarchar(100) NULL`, `ExternalColor nvarchar(200) NULL`, `VariantUrl nvarchar(2000) NULL`, `MatchStatus nvarchar(30)`, `MatchMethod nvarchar(30)`, `MatchConfidence decimal(5,2) NULL`, `CreatedAt datetime2 DEFAULT sysutcdatetime()`, `UpdatedAt datetime2 DEFAULT sysutcdatetime()`. |
| `PriceObservations` | `Id bigint PK`, `ProductMatchId bigint`, `ProductVariantMatchId bigint NULL`, `ObservedAt datetime2`, `RegularPrice decimal(18,2) NULL`, `CurrentPrice decimal(18,2)`, `PreviousPrice decimal(18,2) NULL`, `MemberPrice decimal(18,2) NULL`, `CurrencyCode char(3) DEFAULT 'NOK'`, `InStock bit NULL`, `StockText nvarchar(500) NULL`, `PromotionText nvarchar(1000) NULL`, `PriceType nvarchar(30) NULL`, `SourceUrl nvarchar(2000)`, `ContentHash varchar(64) NULL`, `CreatedAt datetime2 DEFAULT sysutcdatetime()`. |

- `ProductMatches.ProductId` → `Products.Id`; `WebsiteId` → `Websites.Id`.
  `UQ_ProductMatches_Product_Website`: `UNIQUE(ProductId, WebsiteId)`.
- `ProductVariantMatches.ProductMatchId` → `ProductMatches.Id`;
  `ProductVariantId` → `ProductVariants.Id`; `UNIQUE(ProductMatchId, ProductVariantId)`.
- `PriceObservations.ProductMatchId` → `ProductMatches.Id`;
  `ProductVariantMatchId` → `ProductVariantMatches.Id`.

Canonical accepted status/method: **`Matched` / `ExactGtin`**. The adapter's
`ExternalVariationId` maps to the actual column `ExternalVariantId`, not a new column.
`ExternalSku` has **no supplied destination column** and is intentionally not persisted.
No confidence, price type or content hash is invented from adapter data.

## Persistence and failure semantics

1. Start an integration run and read bounded work; close the SQL connection.
2. With no existing match, discover externally. With a usable match, poll its persisted URL
   and verify the expected product/variants, rather than rescan all sitemaps.
3. Validate the complete crawl result with `MonitoringResultValidator.Validate` **before opening SQL**.
   Every selected variant must have one exact identity match and one valid priced observation.
4. Open one short serializable target transaction, recheck target/subscription/product/website and selected
   variant eligibility/GTIN, lock match keys with `UPDLOCK,HOLDLOCK`, upsert product and variant
   identities, append every observation, then commit. Never hold this transaction during HTTP.
5. Complete the integration run independently.

Product/variant upserts reuse the returned existing IDs on reruns. Key-range locks held until
commit protect the authoritative unique keys against concurrent inserts. Variant locks are
acquired in internal variant-ID order. `FirstMatchedAt` is immutable after insertion;
`LastVerifiedAt` is the successful crawler observation time (maximum for multiple variants).
Metadata may be updated, but established external IDs/GTINs and product URL must not be silently
reassigned. A conflicting identity causes the entire transaction to fail and roll back.
SQL identity conflict errors 51000/51001 are translated after rollback to `InvalidDataException`,
so the runner reports `RequiresRequalification` rather than a generic persistence failure.

`ObservedAt` is the adapter's timestamp converted to UTC, not persistence-time "now".
`CreatedAt`/`UpdatedAt` use database UTC defaults/expressions. Prices, availability, text, currency,
URLs and external identities are typed SQL parameters; prices use `decimal(18,2)`.
Each new explicit successful run may append a new observation even if price/content is unchanged.
There is no observation deduplication or new unique constraint; old observations are never updated.

An existing noncanonical/unverified match, missing external product identity, broken/unsafe URL,
missing selected verified variant or
GTIN mismatch is **not treated as absence**: the store throws a qualification-required error.
These conditions raise `InvalidDataException` for the runner's `RequiresRequalification` outcome.
This preserves stale identities and avoids blind rediscovery/replacement. Failed, ambiguous,
incomplete or unmatched crawl results cannot create accepted matches or observations.
SQL failures and cancellation roll back target writes; rollback uses a noncancelled cleanup token.
Normal open/read/write/commit operations forward the caller token. This is not a distributed
transaction: integration reporting failure cannot undo already committed historical observations.

## SmartDrift_Integration.dbo

| Table | Columns |
| --- | --- |
| `Integrations` | `Id int PK`, `[Key] nvarchar(100)`, `Name nvarchar(200)`, `Description nvarchar(1000) NULL`, `IntegrationType nvarchar(50)`, `SourceSystem nvarchar(100) NULL`, `TargetSystem nvarchar(100) NULL`, `HandlerKey nvarchar(150)`, `IsActive bit DEFAULT 1`, `ScheduleExpression nvarchar(100) NULL`, `CreatedAt datetime2`, `UpdatedAt datetime2`. |
| `IntegrationRuns` | `Id bigint PK`, `IntegrationId int`, `CorrelationId uniqueidentifier DEFAULT newid()`, `TriggerType nvarchar(30)`, `TriggeredBy nvarchar(200) NULL`, `Status nvarchar(30)`, `ScheduledAt datetime2 NULL`, `StartedAt datetime2 NULL`, `CompletedAt datetime2 NULL`, `HeartbeatAt datetime2 NULL`, `ItemsDiscovered int DEFAULT 0`, `ItemsProcessed int DEFAULT 0`, `ItemsSucceeded int DEFAULT 0`, `ItemsSkipped int DEFAULT 0`, `ItemsFailed int DEFAULT 0`, `WarningCount int DEFAULT 0`, `ErrorCode nvarchar(100) NULL`, `ErrorSummary nvarchar(2000) NULL`, `WorkerInstance nvarchar(200) NULL`, `CreatedAt datetime2 DEFAULT sysutcdatetime()`, `SourceCheckpoint nvarchar(500) NULL`. |
| `IntegrationRunMessages` | `Id bigint PK`, `IntegrationRunId bigint`, `CreatedAt datetime2 DEFAULT sysutcdatetime()`, `Severity nvarchar(20)`, `Code nvarchar(100) NULL`, `Message nvarchar(2000)`, `EntityType nvarchar(100) NULL`, `EntityId nvarchar(200) NULL`, `DetailsJson nvarchar(MAX) NULL`. |

`IntegrationRuns.IntegrationId` identifies `Integrations.Id`, and messages identify
`IntegrationRuns.Id`. No additional integration uniqueness constraint or status CHECK constraint
was supplied; none is assumed or created. Startup requires **exactly one active** integration with
key **`price-monitor-observation`**; absent/duplicate active configuration fails explicitly.

Runs start `Running`, `TriggerType = Manual`, with UTC start/heartbeat. Completion updates counts
and UTC completion/heartbeat to canonical **`Succeeded`**, **`SucceededWithWarnings`** or **`Failed`**.
`NoMatch`, `Ambiguous`, `Incomplete` are skipped targets/warnings; other non-success outcomes fail.
`ItemsDiscovered` records requested bounded work; `ItemsProcessed` records actual outcome count,
which can differ from the request count on input failure or cancellation.
Messages identify `EntityType = MonitoringTarget` and the target's numeric ID. Codes, diagnostics
and error summaries are bounded to the supplied sizes. Routine target successes are not logged;
one final `RunSummary` message stores created/reused match counts and observation count in
`DetailsJson`, not invented run columns. Completion update and final summary are one transaction.
On cancellation, the orchestrator supplies a bounded noncancelled finalization token.

### Manual integration registration

No schema DDL is needed for the supplied contracts. Missing integration registration is
**configuration DML**, not a migration. A database owner may run the following manually in
`SmartDrift_Integration`, after confirming the `IntegrationType`/`HandlerKey` registration values
against the environment's runtime conventions. These two required values were not supplied as
an authoritative existing registration; the Worker does not guess or provision them.

```sql
-- Set these to approved environment registration values before executing:
DECLARE @IntegrationType nvarchar(50) = NULL;
DECLARE @HandlerKey nvarchar(150) = NULL;
IF @IntegrationType IS NULL OR @HandlerKey IS NULL
    THROW 51010, 'Supply approved IntegrationType and HandlerKey.', 1;
IF EXISTS (SELECT 1 FROM dbo.Integrations WHERE [Key] = N'price-monitor-observation')
    THROW 51011, 'Existing registration must be reviewed, not duplicated.', 1;

INSERT dbo.Integrations
    ([Key], Name, Description, IntegrationType, SourceSystem, TargetSystem,
     HandlerKey, IsActive, ScheduleExpression, CreatedAt, UpdatedAt)
VALUES
    (N'price-monitor-observation', N'Price Monitor observations',
     N'Explicit bounded price observation runs', @IntegrationType, N'PriceMonitor',
     N'SmartDrift_PriceMonitor', @HandlerKey, 1, NULL,
     SYSUTCDATETIME(), SYSUTCDATETIME());
```

## Explicit local acceptance (not normal automated tests)

The first authoritative target is **2**, subscription **2**, product **35148**, website **1**
(`platou-sport`), M variant **54618**, GTIN **`0623555782018`**. Do not use target 1 or
product 35149. XL variant **54620**, GTIN `0623555782032`, is separate; an exact M-only selection
can be accepted without requiring XL. Previously live-verified external IDs are product
`392833`, M variant `392835`; the actual current price must come from the adapter, never hard-coded.
See README for the exact opt-in one-shot command and connection configuration.

Configure `ConnectionStrings:PriceMonitor` and `ConnectionStrings:Integration` locally, then
run the bounded M-only acceptance command (without fixture mode):

```sh
dotnet run --project src/Stadion.SmartDrift.PriceMonitor.Crawler -- \
  --run persisted-monitoring --monitoring-target-id 2 --gtin 0623555782018
```

Omitting `--gtin` selects all currently eligible variants for that target. Explicit `--offline-fixture`
rehearsals do not connect to SQL or persist disk history and are not database acceptance runs.

Run these queries in `SmartDrift_PriceMonitor` before/after the explicit M-only command:

```sql
SELECT t.Id AS TargetId, s.Id AS SubscriptionId, p.Id AS ProductId,
       w.Id AS WebsiteId, w.[Key], v.Id AS VariantId, v.Gtin,
       COALESCE(NULLIF(v.SizeInfo, N''), v.SizeName) AS Size
FROM dbo.MonitoringTargets t
JOIN dbo.MonitoringSubscriptions s ON s.Id = t.MonitoringSubscriptionId
JOIN dbo.Products p ON p.Id = s.ProductId
JOIN dbo.Websites w ON w.Id = t.WebsiteId
JOIN dbo.ProductVariants v ON v.ProductId = p.Id
WHERE t.Id = 2 AND t.Status = N'Active' AND s.Status = N'Active'
  AND w.IsActive = 1 AND p.SourceActive = 1 AND v.SourceActive = 1
  AND s.ValidFrom <= SYSUTCDATETIME()
  AND (s.ValidTo IS NULL OR s.ValidTo > SYSUTCDATETIME())
  AND v.Gtin = N'0623555782018';

SELECT COUNT(*) AS ProductMatchCount
FROM dbo.ProductMatches WHERE ProductId = 35148 AND WebsiteId = 1; -- exactly 1

SELECT m.Id, m.ExternalProductId, m.ExternalName, m.ProductUrl,
       m.MatchStatus, m.MatchMethod, m.FirstMatchedAt, m.LastVerifiedAt,
       vm.Id AS VariantMatchId, vm.ProductVariantId, vm.ExternalVariantId,
       vm.ExternalGtin, vm.ExternalSize, vm.MatchStatus AS VariantStatus,
       vm.MatchMethod AS VariantMethod
FROM dbo.ProductMatches m
JOIN dbo.ProductVariantMatches vm ON vm.ProductMatchId = m.Id
WHERE m.ProductId = 35148 AND m.WebsiteId = 1 AND vm.ProductVariantId = 54618;
-- Exactly one M row; product 392833, variant 392835; Matched / ExactGtin.

SELECT o.Id, o.ProductMatchId, o.ProductVariantMatchId, o.ObservedAt,
       o.RegularPrice, o.CurrentPrice, o.CurrencyCode, o.InStock, o.SourceUrl, o.CreatedAt
FROM dbo.PriceObservations o
JOIN dbo.ProductMatches m ON m.Id = o.ProductMatchId
JOIN dbo.ProductVariantMatches vm ON vm.Id = o.ProductVariantMatchId
WHERE m.ProductId = 35148 AND m.WebsiteId = 1 AND vm.ProductVariantId = 54618
ORDER BY o.Id DESC;
-- New row: actual live price, NOK, live availability/URL and crawler UTC ObservedAt.
```

Then in `SmartDrift_Integration`:

```sql
SELECT TOP (10) r.Id, r.Status, r.TriggerType, r.StartedAt, r.CompletedAt,
       r.ItemsDiscovered, r.ItemsProcessed, r.ItemsSucceeded,
       r.ItemsSkipped, r.ItemsFailed, r.WarningCount, r.ErrorCode, r.ErrorSummary
FROM dbo.IntegrationRuns r
JOIN dbo.Integrations i ON i.Id = r.IntegrationId
WHERE i.[Key] = N'price-monitor-observation'
ORDER BY r.Id DESC;
-- First successful bounded run: Succeeded, Manual, completed;
-- discovered/processed/succeeded = 1; skipped/failed/warnings = 0.

SELECT TOP (20) msg.*
FROM dbo.IntegrationRunMessages msg
JOIN dbo.IntegrationRuns r ON r.Id = msg.IntegrationRunId
JOIN dbo.Integrations i ON i.Id = r.IntegrationId
WHERE i.[Key] = N'price-monitor-observation'
ORDER BY msg.Id DESC;
```

On the second successful execution, product and M variant match IDs/counts and `FirstMatchedAt`
remain unchanged; observation history gains a row and a separate completed integration run
records the reused identities. Automated SQL command/transaction tests use `DbConnection`/
`DbCommand` test doubles and require neither live SQL/HTTP nor credentials; actual database
constraint/locking behavior remains covered by this explicit environment acceptance.
