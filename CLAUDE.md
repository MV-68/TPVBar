# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

TPVBar: point-of-sale for a single bar, run on a LAN (Windows server + browsers on PCs/tablets). UI text and docs are in Spanish (es-ES); code identifiers are in English. Status: release candidate, **not fiscally validated** (no real hardware/AEAT testing) — do not present it as ready for real billing. Full spec: `Prompt_Maestro_TPVBar.md` (referenced by README; not in git).

## Commands

```powershell
dotnet tool restore
powershell -ExecutionPolicy Bypass -File scripts\dev-db.ps1      # dev PostgreSQL on port 5433 (data in .pgdata) + user secrets
dotnet build
dotnet test                                                      # xUnit v3 on Microsoft.Testing.Platform (set in global.json)
dotnet run --project src/TPVBar.Web --launch-profile http        # http://localhost:5141, GET /health
$env:DOTNET_ENVIRONMENT="Development"; dotnet run --project src/TPVBar.PrintAgent   # print agent, separate terminal
powershell -ExecutionPolicy Bypass -File scripts\demo.ps1        # demo with fake data
```

- Integration tests (`tests/TPVBar.Integration.Tests`) need the dev PostgreSQL running (real Postgres, no SQLite/Testcontainers).
- Single test: with MTP, filter via `dotnet test --project tests/TPVBar.Domain.Tests --filter-method "*Name*"` (also `--filter-class`); verify flags with `dotnet test --help` if unsure.
- Migrations: `dotnet tool run dotnet-ef migrations add <Name> --project src/TPVBar.Infrastructure --startup-project src/TPVBar.Web --output-dir Persistence/Migrations`. Applied automatically in Development; elsewhere only via `dotnet run --project src/TPVBar.Web -- --migrate` (D-014). Idempotent SQL scripts go to `docs/database/`.
- Deployment/ops scripts are in `scripts/` (install-server, update-server, backup, restore, publish, AEAT preproduction check); guides in `docs/operations/`.

## Build rules

`Directory.Build.props`: net10.0, C# 14, nullable, **warnings are errors**, `AnalysisMode=Recommended`, code style enforced in build (`.editorconfig`). Package versions are centralized in `Directory.Packages.props`; `nuget.config` pins nuget.org only. Do not bump EF Core to 11 alone — Npgsql only ships 10.x stable (D-003); update the EF/ASP.NET family together.

## Architecture

Modular monolith in layers (D-001): `Domain ← Application ← Infrastructure`; `Web` composes everything; `PrintAgent` is separate. Architecture tests in `tests/*/ArchitectureTests.cs` enforce dependency rules.

- **Domain**: entities and rules, no dependencies.
- **Application**: use cases as `Features/<Area>/*Service.cs` (Authentication, Cash, Catalog, Inventory, Invoicing, Kitchen, Orders, Printing, Reports). It has **no EF Core reference**: data access goes through `IAppData` (exposes `IQueryable` + async executors) and `IUnitOfWork` (D-023), so services are unit-tested with in-memory fakes (`tests/TPVBar.Application.Tests/Fakes`).
- **Infrastructure**: EF Core/Npgsql (`Persistence/TpvBarDbContext`, Migrations, Seed), security (password hashing with Identity's `PasswordHasher` on a custom `AppUser`, no Identity tables — D-009), `Billing` (AEAT submission).
- **Web**: Blazor Web App (interactive Server) + minimal-API endpoints + SignalR hub `/hubs/operations`. Blazor circuits are long-lived, so `UseCaseRunner` creates a fresh DI scope/DbContext per UI action with the session identity and retries concurrency conflicts (D-022). Login is a static (non-interactive) page because the auth cookie can only be written in a normal HTTP request (D-010). Realtime events are data-less notifications; Blazor uses an in-process bus, other clients use SignalR (D-029/D-030).
- **PrintContracts / PrintAgent**: the agent is a Windows service that talks to the server only through the API defined in PrintContracts (API key required, no default — D-033); it never touches the DB. Print jobs are a persistent queue with lease and at-least-once delivery; "printed" is never claimed without agent confirmation (D-026–D-028).

## Cross-cutting invariants (see DECISIONS.md, D-001…D-069)

`DECISIONS.md` is the source of truth for design rationale — read the relevant D-xxx before changing behaviour in that area, and add new decisions there (and entries in `CHANGELOG.md`).

- Money is `decimal`; instants are UTC `DateTimeOffset`, `Europe/Madrid` only for presentation/business-day logic.
- Nothing confirmed is deleted: use cancellations/compensations. Payments and invoices/billing records are immutable (D-035, D-062); corrections are new records (D-069).
- Idempotency on payments and AEAT submissions; optimistic concurrency via Postgres `xmin`.
- Stock is always the sum of its movements; deducted on *send* to kitchen, restored on cancel; no negative stock without admin authorization (D-043–D-046).
- Audit log (`audit_events`) is immutable via DB trigger; details must never contain passwords/hashes (D-012).
- Auth: cookie, permission-based policies (`Policies.For(permission)`), default policy requires login unless `[AllowAnonymous]`; account lockout after 5 failures (D-010, D-011).
- Invoicing: simplified/complete/rectifying invoices with a single hash-chained registry serialized on the chain head (D-061); embedded official XSD validation before sending; AEAT (VERI*FACTU) submission is **disabled by default**, mode set only from server config, ordered queue that stops on rejection (D-065–D-067).
- Three DB roles with least privilege (`scripts/db-roles.sql`, `db-grants.sql`; D-052). Secrets never in the repo: User Secrets in dev, protected config file/env vars in production (D-053). HTTPS mandatory in production (D-055).
- Reports: no mixing of magnitudes; aggregation in memory; CSV export only, no XLSX (D-048–D-051).
