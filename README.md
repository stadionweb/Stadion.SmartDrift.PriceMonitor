# Stadion.SmartDrift.PriceMonitor

Price Monitor is a separate SmartDrift service for collecting external market-price observations for products selected for monitoring. Its crawler discovers potential external product matches and records market facts; it does not decide or change prices. The repository may later support broader Price Intelligence.

## Solution

- `src/Stadion.SmartDrift.PriceMonitor.Core`: domain contracts, independent of infrastructure.
- `src/Stadion.SmartDrift.PriceMonitor.Infrastructure`: SQL monitoring persistence and IntegrationRun reporting.
- `src/Stadion.SmartDrift.PriceMonitor.Crawler`: .NET Worker Service executable and website crawler registration/selection.
- `tests/Stadion.SmartDrift.PriceMonitor.Tests`: xUnit contract, registration and actual-process smoke tests.
- `docs/architecture.md` and `docs/database-contract.md`: intended flow and known database relationships.

Requires the .NET 10 SDK:

```sh
dotnet restore Stadion.SmartDrift.PriceMonitor.sln
dotnet build Stadion.SmartDrift.PriceMonitor.sln --no-restore
dotnet test Stadion.SmartDrift.PriceMonitor.sln --no-build
dotnet run --project src/Stadion.SmartDrift.PriceMonitor.Crawler
dotnet run --project src/Stadion.SmartDrift.PriceMonitor.Crawler -- --run unknown
```

Default startup constructs the host and crawler registry, contacts no websites and exits successfully. `--run <crawler-key>` selects a key case-insensitively; an unknown key or invalid arguments exit with code 2. The registered `platou-sport` adapter is not executed by selection alone: no monitoring-target input is configured. No scheduler is running.

## Platou Sport discovery spike

The explicit control-target run is opt-in and contacts the real site:

```sh
dotnet run --project src/Stadion.SmartDrift.PriceMonitor.Crawler -- --run platou-sport --control-target
```

It uses the Beta SL Jacket M / Black GTIN `0623555782018` fixture and prints a JSON `CrawlResult`; exit codes are 0 for success, 1 for failed/incomplete/not found, and 2 for invalid selection. The fixture IDs are placeholders, not database IDs. No observed price is hard-coded. Normal tests use fake HTTP responses and do not access the internet.

Discovery checks robots.txt, reads the sitemap index and only product sitemaps, and fetches product pages sequentially with the `StadionSmartDriftPriceMonitor/1.0` user agent. It honors applicable allow/disallow and crawl-delay rules, refuses redirects, limits responses to 2 MB, uses a 20-second HTTP timeout, and waits at least one second between requests. By default it inspects at most 30 product sitemaps and 500 product pages. To adjust the bounds for a local run, set `PlatouSport__MaxSitemaps` (1–100) and `PlatouSport__MaxProductPages` (1–100000). Product-name terms prioritize URLs but are not required to occur in the URL. A bounded search can be incomplete.

The parser understands JSON-LD `Product`/`ProductGroup` and WooCommerce `data-product_variations` linked to the JSON-LD product URL. WooCommerce variant SKU, ID, size, price and stock come from the variation payload; currency comes from the unambiguous product offer. Multiple target variants can resolve to one product candidate, with separate variant matches linked by product URL and input variant ID. Only an unambiguous `ExactGtin` with nonconflicting available size/color can produce a variant price; article-number and exact brand/name matches are explicitly weaker product candidates without a price. Unavailable or unsupported fields remain null. `Incomplete` indicates partial or unsupported data; `Ambiguous` indicates duplicate matching identifiers or forms. The sanitized fixture contains a minimized reconstruction of fields supplied from a locally captured response, alongside the earlier synthetic JSON-LD fixture. The captured price is test data, not a live expectation. Live end-to-end validation remains local.

The control-target command remains a nonpersisting discovery spike. Persisted monitoring uses discovery for the first match and verified direct-URL polling for subsequent runs.

The smoke tests start the Worker via `dotnet run --no-build`, using project path and configuration embedded by MSBuild, not a path guessed from the test runner. They capture stdout, stderr and exit code on failure. They are intended to run with both `dotnet test` and Visual Studio Test Explorer after building the projects.

## First persisted monitoring observation

Configure `ConnectionStrings:PriceMonitor` for SmartDrift_PriceMonitor and `ConnectionStrings:Integration` for SmartDrift_Integration through User Secrets or environment variables (`ConnectionStrings__PriceMonitor`, `ConnectionStrings__Integration`). Register the active `price-monitor-observation` Integration manually using the SQL in `docs/database-contract.md`. The Worker never creates or migrates database schema.

Run the authoritative first target and **only the M variant**:

```sh
dotnet run --project src/Stadion.SmartDrift.PriceMonitor.Crawler -- --run persisted-monitoring --monitoring-target-id 2 --gtin 0623555782018
```

This reads active subscription 2 / target 2 / website 1 and Product 35148 / ProductVariant 54618 from the database; these identities are not hard-coded into the Worker. Website.Key chooses the adapter. Active source products/variants and subscription validity dates are also required. Website defaults never generate work. Omitting `--gtin` selects all active variants of the **one explicitly specified target**, not every target; use the GTIN filter for first acceptance.

After external HTTP completes, a short target transaction upserts `ProductMatches` by `(ProductId, WebsiteId)` and `ProductVariantMatches` by `(ProductMatchId, ProductVariantId)`, then appends observations with the adapter's price, currency, availability, source URL and UTC observation time. Canonical match values are `Matched` / `ExactGtin`. Reruns reuse match IDs and append new historical facts, even when price/content is unchanged. There is no content-hash deduplication or automatic replay: another explicit successful invocation may append another observation, including at an identical supplied timestamp.

A verified existing match is polled at its persisted ProductUrl without sitemap discovery. Product ID, selected variant GTIN and external variant ID must still agree. Broken, unverified or conflicting persisted identities require explicit requalification; they are not silently replaced. NoMatch, Ambiguous and Incomplete discovery results write no accepted matches or prices. Failed targets preserve previous committed data.

Every invocation opens an established IntegrationRun (`Running`), records target-level warning/error messages, and completes with `Succeeded`, `SucceededWithWarnings` or `Failed`. The JSON console summary includes target outcomes, observations written and matches created/reused. Existing IntegrationRuns counts track eligible targets discovered, processed, succeeded, skipped, failed and warnings; extra match/observation counts are recorded in a single RunSummary message. A missing/inactive Integration fails before crawling. Exit codes: **0** for successful persisted observation, **1** for unsuccessful/cancelled execution, **2** for invalid command or missing connection-string configuration. Default startup still contacts neither SQL nor websites. No scheduler is installed.

Cancellation propagates through reads, HTTP and persistence. A target's writes are committed together or rolled back together. Run finalization after cancellation uses a separate bounded cleanup token so the cancelled crawl token does not leave the execution in `Running`. Reporting occurs after target commit across the two databases: an Integration reporting failure does not undo a committed observation. Inspect persisted data before deliberately retrying.

### Local acceptance

Use the eligibility, match, observation and run queries in `docs/database-contract.md` before and after the command above. Confirm Product 35148 / Website 1 has one ProductMatch, variant 54618 has one ProductVariantMatch with ExternalVariantId `392835`, and a new observation refers to both match IDs with live-parsed NOK price and the actual Platou URL. Run again: match counts remain one, observation history grows, and another completed IntegrationRun exists. The known external product ID is `392833`; the earlier live price is **not** an acceptance constant. Live database/site acceptance must be performed locally with authorized configuration.

### Offline executable coverage

Normal tests exercise the actual Worker executable without internet or SQL using:

```sh
dotnet run --project src/Stadion.SmartDrift.PriceMonitor.Crawler -- --run persisted-monitoring --monitoring-target-id 2 --gtin 0623555782018 --offline-fixture /absolute/path/to/fixture.json
```

This explicitly marked rehearsal replaces SQL/run stores with ephemeral doubles and HTTP with fixture responses, and runs the same command/runner/Platou parser path. It never writes a database or history file and is **not** persisted acceptance. JSON contains `Work` (MonitoringWorkItem), `Responses` (absolute URL → response body) and optionally `Match` (VerifiedProductMatch for polling). Executable tests generate temporary fixtures from the sanitized captured HTML and test discovery, polling, stale identity and unknown Website.Key outcomes. SQL-boundary tests separately exercise production commands, parameter bindings, identity reuse and transaction rollback using database doubles.

## Configuration and secrets

Standard .NET configuration and logging are used. Do not commit credentials, API keys, cookies or tokens; connection-string values must not be committed. Use User Secrets or environment configuration for local credentials and environment/server configuration or an appropriate secret store in production. The SQL client uses encrypted connections by default; configure valid server certificates rather than bypassing certificate validation.

## Development workflow

Discussion / architecture decision → GitHub Issue → Copilot coding agent → Pull Request → review → merge → local Visual Studio validation → precise feedback. GitHub is the source of truth; application code should not routinely be copied manually into Visual Studio.
