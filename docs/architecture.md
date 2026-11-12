# Architecture

```text
SmartDrift
  | user selects products and websites
  v
SmartDrift_PriceMonitor
  | active monitoring configuration
  v
Price Monitor Crawler
  +--> website adapter --> external website
  +--> ProductMatches
  +--> ProductVariantMatches
  +--> append-only PriceObservations
  +--> SmartDrift_Integration reporting
```

The crawler is a separate executable service. Core owns small observation and crawler contracts and has no SQL, HTTP implementation, browser, website adapter or SmartDrift runtime dependency. Infrastructure is the future home for external-system concerns. The Worker uses the Generic Host, configuration, dependency injection and logging. Website adapters will implement `IWebsiteCrawler` with a stable unique key and accept a target and cancellation token. Different sites may need HTTP, search endpoints, structured product data, sitemaps, HTML parsing or, only if justified, browser automation. Concrete integrations should drive shared abstractions.

The host starts and stops once. The `platou-sport` adapter is registered; selection alone does not run it without monitoring work. An explicit control-target option runs the fixture target against the live website without persistence. The single-target persisted command requires one positive MonitoringTarget ID and optionally filters its active variants by GTIN. The explicit `persisted-monitoring-batch` command selects active explicit targets by optional Website key, orders them by target ID, and enforces a 100,000 hard maximum (or a smaller `--max-targets`). Keys are unique case-insensitively. The registry forwards real bigint product/variant identities and cancellation to adapters. Unknown Website keys in persisted work produce a target-level `UnknownWebsiteAdapter` failure, not a guessed adapter.

Platou Sport discovery is bounded and sequential: robots.txt, product sitemap index entries, then product-page inspection. Product candidates are distinct from `ExactGtin` variant candidates, which carry the input variant ID and product URL; multiple sizes on one page do not create multiple product candidates. Weaker matches are labeled as candidates and do not produce variant prices. A partial set of matches or prices is incomplete. The parser supports JSON-LD and WooCommerce variation data, tested against a minimized reconstruction of locally supplied captured fields. Live validation is separate. Polling reads robots.txt and the verified persisted product URL directly; it verifies the external product and selected variant identities and retains discovery's origin, redirect, timeout, size and robots protections. A broken match produces `RequiresRequalification` rather than automatic discovery/reassignment.

## Matching and observations

Monitoring is selected at main-product level. Product variants supply technical matching inputs such as GTIN/EAN, size and color. Future matching should prioritize:

1. Exact GTIN/EAN.
2. Exact producer/supplier article number.
3. Brand plus model/product identity.
4. Variant attributes such as size and color.
5. Fuzzy or AI matching only when stable identifiers are insufficient.

Uncertain matches should be returned as candidates, not silently decided. Observations are neutral facts such as regular, current, previous and member prices, currency, availability, promotion text, source URL and observation time.

Crawler = observation. It does not make or execute pricing decisions. Future Price Intelligence may interpret observations; a future store-level Pricing Engine may consume observations or recommendations as a separate capability with explicit business rules and guardrails.

## Persistence and operational reporting

SmartDrift owns monitoring configuration; the Worker reads only explicit active MonitoringTargets with active subscriptions/websites and eligible source variants. IsDefaultForMonitoring is never a queue signal. Core defines the monitoring/persistence boundaries without SQL dependencies. Infrastructure implements the authoritative SQL contract using typed parameters; the Worker coordinates adapter selection and target-level outcomes.

External HTTP runs outside any SQL transaction. Only a complete, unambiguous ExactGtin result enters a short target transaction: recheck eligibility, lock/upsert the unique product/website identity, lock/upsert variant identities, append observations, commit. Conflicting identities fail rather than reassign. Failed or cancelled transactions roll back, preserving earlier records. Matches are idempotent; observations are append-only per explicit invocation without content/time deduplication.

SmartDrift_Integration's existing Integrations, IntegrationRuns and IntegrationRunMessages represent execution history. An active manually registered `price-monitor-observation` Integration is required. The run starts before work is read and completes after target persistence, with canonical statuses and counts. Target warnings/errors carry MonitoringTarget entity IDs; one RunSummary message carries additional match/observation counts. These databases are not enlisted in a distributed transaction: reporting failure cannot undo an already committed target fact. Cancellation stops future work and uses bounded cleanup for terminal operational reporting.

Batch work runs sequentially. If a ProductMatch exists, only active Variants with verified ProductVariantMatches are polled; otherwise discovery uses one active non-null-GTIN representative, preferring size M and then lowest ProductVariant ID. This avoids an uncontrolled first-run variant fan-out while preserving existing direct-polling and persistence behavior. Target-level negative, incomplete, ambiguous, requalification and failure outcomes are reported and the batch continues. Each batch invocation owns exactly one IntegrationRun.

Schema is manually owned; there are no automatic migrations, recurring schedules, frontend changes or broad parallel crawls. See README for the explicit batch command and local acceptance flow, and database-contract.md for schemas and persistence semantics.
