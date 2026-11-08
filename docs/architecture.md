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

The host starts and stops once. The `platou-sport` adapter is registered; selection alone does not run it without monitoring work. An explicit control-target option runs the fixture target against the live website. Keys are unique case-insensitively. Unknown keys and malformed selection return exit code 2. The registry forwards targets and cancellation to adapters.

Platou Sport discovery is bounded and sequential: robots.txt, product sitemap index entries, then product-page inspection. Exact GTIN results carry the input variant ID, while weaker matches are labeled as candidates and do not produce variant prices. The initial parser handles JSON-LD only; its synthetic product fixture is not evidence that the current live site exposes this format. Live validation is separate. Future polling must use a previously verified persisted match URL rather than rerun discovery; neither persistence nor polling is implemented.

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
