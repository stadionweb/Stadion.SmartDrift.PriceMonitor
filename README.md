# Stadion.SmartDrift.PriceMonitor

Price Monitor is a separate SmartDrift service for collecting external market-price observations for products selected for monitoring. Its crawler discovers potential external product matches and records market facts; it does not decide or change prices. The repository may later support broader Price Intelligence.

## Solution

- `src/Stadion.SmartDrift.PriceMonitor.Core`: domain contracts, independent of infrastructure.
- `src/Stadion.SmartDrift.PriceMonitor.Infrastructure`: boundary for future infrastructure and SQL integration.
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

The spike accepts exactly one target variant per call. The parser currently understands JSON-LD `Product`/`ProductGroup`, `hasVariant` and a single unambiguous `Offer`. Only an exact GTIN with nonconflicting available size/color can produce a variant price; article-number and exact brand/name matches are explicitly weaker product candidates without a price. Unavailable or unsupported fields remain null. `Incomplete` distinguishes limits, unsupported pages, weaker candidates or a match without a usable offer from `NotFound` and `Failed`. The HTML fixture is synthetic representative JSON-LD, **not a captured Platou product page**: the actual website's product/variant format and live end-to-end result still need local validation. Update the parser and sanitized fixtures if the real markup differs; do not infer a price or variant from a page that lacks evidence.

This is first-time discovery only. Future polling should fetch a verified, persisted product/variant URL directly rather than repeat sitemap discovery; persistence and polling are not implemented.

The smoke tests start the Worker via `dotnet run --no-build`, using project path and configuration embedded by MSBuild, not a path guessed from the test runner. They capture stdout, stderr and exit code on failure. They are intended to run with both `dotnet test` and Visual Studio Test Explorer after building the projects.

## Future input and reporting

SmartDrift users select main products and websites to monitor. Active `MonitoringTargets` in SmartDrift_PriceMonitor will become the crawler work queue, with variants providing identifiers and attributes for matching. Results are intended to become product/variant match candidates and historical price observations. Crawler executions will later report operational status through SmartDrift_Integration (`Integrations`, `IntegrationRuns`, `IntegrationRunMessages`); no SQL access is implemented here.

## Configuration and secrets

Standard .NET configuration and logging are used. Do not commit credentials, API keys, cookies or tokens; any future connection-string values in committed configuration must be empty placeholders. Use User Secrets or environment configuration for local credentials and environment/server configuration or an appropriate secret store in production.

## Development workflow

Discussion / architecture decision → GitHub Issue → Copilot coding agent → Pull Request → review → merge → local Visual Studio validation → precise feedback. GitHub is the source of truth; application code should not routinely be copied manually into Visual Studio.
