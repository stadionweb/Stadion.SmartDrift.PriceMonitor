# Database contract and future input

This is the known domain contract, not a database migration or an exhaustive schema. Verify the actual schema before implementing access; do not infer unspecified column names, types, keys or relationships from this document. No tables or SQL queries are created here.

## SmartDrift_PriceMonitor

| Entity | Purpose and known identity relationships |
| --- | --- |
| `Products` | Monitored main products. `Id` is the target product identity; `SourceSystem` + `SourceItemId` identify the source product. |
| `ProductVariants` | Variants used as technical matching inputs. `Id` is the target variant identity; `ProductId` → `Products.Id`; `SourceItemSubId` identifies the source variant; `Gtin` is a matching identifier. |
| `Websites` | Configured external websites. |
| `MonitoringSubscriptions` | Persisted main-product monitoring state; `ProductId` → `Products.Id`. |
| `MonitoringTargets` | Links a monitoring subscription to a website. Active targets will become the crawler work queue. |
| `ProductMatches` | Future external product matches or match candidates. |
| `ProductVariantMatches` | Future external variant matches or match candidates. |
| `PriceObservations` | Future historical observations of external market prices and related facts. |

SmartDrift users select products at main-product level and choose websites. The future crawler reads active targets and their related subscriptions, products, variants and websites; variants supply GTIN/EAN and other matching attributes. The source and target identities are distinct. No persistence or queue implementation exists yet.

## SmartDrift_Integration

Future crawler runs will report operational status using `Integrations`, `IntegrationRuns` and `IntegrationRunMessages`. Their detailed schema and runtime behavior must be verified before implementation. This repository does not implement their persistence or copy the existing Integration Runtime.
