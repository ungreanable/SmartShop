# Contributing to SmartShop

ขอบคุณที่สนใจร่วมพัฒนา! / Thanks for helping out!

## Getting started

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download) and Docker (Docker Desktop / Podman).

```bash
dotnet tool restore

# Backing services
docker compose -f deploy/docker-compose.dev.yml up -d

# API (:5080, applies migrations on start), worker (:5090), web host (:5100, open this one)
dotnet run --project src/Hosts/SmartShop.Api --launch-profile http
dotnet run --project src/Hosts/SmartShop.Worker --launch-profile http
dotnet run --project src/Clients/SmartShop.Web --launch-profile http
```

Alternatively `dotnet run --project src/Aspire/SmartShop.AppHost` starts everything with the Aspire dashboard.

In development you can sign in without LINE: the web app shows a **Dev login** button
(`Auth:DevLogin:Enabled=true`, never available in Production). `scripts/dev-api.sh` has helpers to call the API
from a shell with dev users (`dev_login`, `api`).

## Tests

```bash
dotnet test --project tests/SmartShop.UnitTests
dotnet test --project tests/SmartShop.ArchitectureTests
dotnet test --project tests/SmartShop.IntegrationTests   # needs Docker (Testcontainers)
dotnet format SmartShop.slnx --verify-no-changes --severity error
dotnet run scripts/check-razor-params.cs -- src/Clients/SmartShop.UI
```

Integration tests boot the real API against PostgreSQL in `Standalone` messaging mode (events handled in-process
through durable local queues) with **row-level security enabled**. External services (LINE, slip verifier, webhook
receivers) are replaced by fakes in `tests/SmartShop.IntegrationTests/Infrastructure`.

## Architecture rules (enforced by `tests/SmartShop.ArchitectureTests`)

- A module never references another module. Cross-module calls go through `SmartShop.Contracts`
  (interfaces for synchronous reads, integration events for everything else).
- Each module owns one PostgreSQL schema and its own EF Core migrations.
- `Domain` code does not depend on EF Core, ASP.NET Core, Wolverine or `SmartShop.Infrastructure`.
- Integration events are sealed records, named in the past tense, published through the outbox
  (`IDbContextOutbox<T>.PublishAsync` + `SaveChangesAndFlushMessagesAsync`).
- Wolverine handler classes are public and end in `Handler`; one `Handle` method per concrete message type
  (Wolverine does not dispatch on interfaces). Handlers that run raw SQL use `[RequiresEagerTransaction]`.
- Every `DateTimeOffset` stored in PostgreSQL must be UTC.

Read [docs/03-architecture.md](docs/03-architecture.md) before larger changes.

## Adding a migration

```bash
scripts/add-migration.sh Ordering AddSomething
```

## Mobile app

```bash
dotnet workload install maui
dotnet build src/Clients/SmartShop.Mobile -f net10.0-android     # needs the Android SDK + JDK 17
dotnet build src/Clients/SmartShop.Mobile -f net10.0-windows10.0.19041.0
```

The app is not part of `SmartShop.slnx` (CI machines without the workload); use `SmartShop.Mobile.slnx`.
Push on Android needs your own `Platforms/Android/google-services.json` (git-ignored).

## UI text

UI strings are written inline in both languages: `@L["ไทย", "English"]`. Keep both.
After changing component parameters run the Razor parameter check above (MudBlazor renames parameters between versions
and unknown parameters fail silently at runtime).

## Licensing

SmartShop is MIT. NuGet dependencies must be permissive (MIT, Apache-2.0, BSD, MPL-2.0).
GPL/AGPL software may only be used as a separate container (see `scripts/check-licenses.sh`).

## Pull requests

- Use [Conventional Commits](https://www.conventionalcommits.org) (`feat(ordering): ...`, `fix(shops): ...`).
- Add or update tests. Integration tests are preferred for endpoint behaviour.
