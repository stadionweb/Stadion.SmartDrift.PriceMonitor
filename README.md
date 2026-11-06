# Stadion.SmartDrift.PriceMonitor

Price Monitor is a separate SmartDrift service for collecting external market-price observations for products selected for monitoring. Its crawler discovers potential external product matches and records market facts; it does not decide or change prices. The repository may later support broader Price Intelligence.

## Solution

- `src/Stadion.SmartDrift.PriceMonitor.Core`: domain contracts, independent of infrastructure.
- `src/Stadion.SmartDrift.PriceMonitor.Infrastructure`: boundary for future SQL, HTTP, parsing and other infrastructure; no live integration yet.
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

Default startup constructs the host and crawler registry, contacts no websites and exits successfully. `--run <crawler-key>` selects a key case-insensitively; an unknown key or invalid arguments exit with code 2. There are currently no registered website crawlers or configured monitoring targets. If a crawler is registered, selecting it only reports that no targets are configured; it does not execute it until a real work-input flow is implemented. No fake loop or scheduler is running.

The smoke tests start the Worker via `dotnet run --no-build`, using project path and configuration embedded by MSBuild, not a path guessed from the test runner. They capture stdout, stderr and exit code on failure. They are intended to run with both `dotnet test` and Visual Studio Test Explorer after building the projects.

## Future input and reporting

SmartDrift users select main products and websites to monitor. Active `MonitoringTargets` in SmartDrift_PriceMonitor will become the crawler work queue, with variants providing identifiers and attributes for matching. Results are intended to become product/variant match candidates and historical price observations. Crawler executions will later report operational status through SmartDrift_Integration (`Integrations`, `IntegrationRuns`, `IntegrationRunMessages`); no SQL access is implemented here.

## Configuration and secrets

Standard .NET configuration and logging are used. Do not commit credentials, API keys, cookies or tokens; any future connection-string values in committed configuration must be empty placeholders. Use User Secrets or environment configuration for local credentials and environment/server configuration or an appropriate secret store in production.

## Development workflow

Discussion / architecture decision → GitHub Issue → Copilot coding agent → Pull Request → review → merge → local Visual Studio validation → precise feedback. GitHub is the source of truth; application code should not routinely be copied manually into Visual Studio.
