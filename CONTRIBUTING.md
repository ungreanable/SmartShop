# Contributing to SmartShop

ขอบคุณที่สนใจร่วมพัฒนา! / Thanks for helping out!

## Getting started

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download) and Docker (Docker Desktop / Podman).

```bash
dotnet tool restore
dotnet run --project src/Aspire/SmartShop.AppHost     # whole stack + Aspire dashboard
dotnet test --project tests/SmartShop.UnitTests
dotnet test --project tests/SmartShop.ArchitectureTests
dotnet test --project tests/SmartShop.IntegrationTests  # needs Docker (Testcontainers)
```

In development you can sign in without LINE: the web app shows a **Dev login** button
(`Auth:DevLogin:Enabled=true`, never available in Production).

## Architecture rules (enforced by `tests/SmartShop.ArchitectureTests`)

- A module never references another module. Cross-module calls go through `SmartShop.Contracts`
  (interfaces for synchronous reads, integration events for everything else).
- Each module owns one PostgreSQL schema and its own EF Core migrations.
- `Domain` code does not depend on EF Core, ASP.NET Core, Wolverine or `SmartShop.Infrastructure`.
- Integration events are sealed records, named in the past tense, published through the outbox
  (`IDbContextOutbox<T>.PublishAsync` + `SaveChangesAndFlushMessagesAsync`).

Read [docs/03-architecture.md](docs/03-architecture.md) before larger changes.

## Adding a migration

```bash
scripts/add-migration.sh Ordering AddSomething
```

## Licensing

SmartShop is MIT. NuGet dependencies must be permissive (MIT, Apache-2.0, BSD, MPL-2.0).
GPL/AGPL software may only be used as a separate container (see `scripts/check-licenses.sh`).

## Pull requests

- Use [Conventional Commits](https://www.conventionalcommits.org) (`feat(ordering): ...`, `fix(shops): ...`).
- Add or update tests. Integration tests are preferred for endpoint behaviour.
- Keep UI text in Thai and English (`src/Clients/SmartShop.UI/Localization`).
