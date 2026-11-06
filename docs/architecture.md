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
  +--> future ProductMatches
  +--> future ProductVariantMatches
  +--> future PriceObservations
  +--> future SmartDrift_Integration reporting
```

The crawler is a separate executable service. Core owns small observation and crawler contracts and has no SQL, HTTP implementation, browser, website adapter or SmartDrift runtime dependency. Infrastructure is the future home for external-system concerns. The Worker uses the Generic Host, configuration, dependency injection and logging. Website adapters will implement `IWebsiteCrawler` with a stable unique key and accept a target and cancellation token. Different sites may need HTTP, search endpoints, structured product data, sitemaps, HTML parsing or, only if justified, browser automation. Concrete integrations should drive shared abstractions.

The current host starts and stops once. It resolves crawler registrations but does not execute a selected crawler without real monitoring work; no website adapters exist. Keys are unique case-insensitively. Unknown keys and malformed selection return exit code 2. The registry exposes target execution and forwards cancellation for future callers.

## Matching and observations

Monitoring is selected at main-product level. Product variants supply technical matching inputs such as GTIN/EAN, size and color. Future matching should prioritize:

1. Exact GTIN/EAN.
2. Exact producer/supplier article number.
3. Brand plus model/product identity.
4. Variant attributes such as size and color.
5. Fuzzy or AI matching only when stable identifiers are insufficient.

Uncertain matches should be returned as candidates, not silently decided. Observations are neutral facts such as regular, current, previous and member prices, currency, availability, promotion text, source URL and observation time.

Crawler = observation. It does not make or execute pricing decisions. Future Price Intelligence may interpret observations; a future store-level Pricing Engine may consume observations or recommendations as a separate capability with explicit business rules and guardrails.

## Future integration

Active `MonitoringTargets` in SmartDrift_PriceMonitor will be the work queue. The service will later read related products, variants, websites and monitoring subscriptions and store results there. Crawler executions will later report operational status through SmartDrift_Integration's `Integrations`, `IntegrationRuns` and `IntegrationRunMessages`. This foundation implements neither SQL persistence nor operational reporting and does not duplicate the Integration Runtime from Stadion.SmartDrift.Integrations. Prefer loose conceptual compatibility until a concrete need for shared code exists.
