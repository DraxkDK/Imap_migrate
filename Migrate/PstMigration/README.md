# PST → Microsoft 365 Migration (Graph, app-only)

Reads local Outlook PST files and recreates folders / mail / contacts / calendar
items in Exchange Online via **Microsoft Graph** using an **app-only certificate**
(Entra ID App Registration). No Outlook required. End users never enter a password.

> **Email migration is best-effort.** Microsoft Graph has no API that ingests a
> whole PST. We parse locally and create individual objects, so original
> timestamps, conversation info, and Exchange internal properties may not be
> preserved. This is **not** equivalent to Microsoft Purview PST Import.

## Zero cloud staging
PST files **stay on the endpoint**. No Azure Storage, Blob, SAS, AzCopy, or Purview
upload. The agent parses locally and streams each item straight to Graph. The
portal stores **metadata only** (hash, size, counts, status, destination ids,
errors) — never bodies or attachment content.

## Architecture (decisions)
- **Auth:** portal holds the certificate and brokers **short-lived Graph tokens**
  to the agent (`/api/agents/graph-token`). The agent never holds the cert; the
  agent calls Graph directly with the token. Revoke an agent to cut it off.
- **Portal DB:** SQLite (EF Core; swap provider later).
- Clean architecture + DI; `IPstParser` / `IGraphMailboxService` are abstractions
  so the PST library and Graph layer can change without touching business logic.

## Solution structure
```
src/
  PstMigration.Domain          enums, entities, normalized models
  PstMigration.Contracts       agent <-> portal DTOs (metadata only)
  PstMigration.Application      interfaces, folder normalizer, fidelity warnings
  PstMigration.PstParser        IPstParser + MockPstParser (real adapter = Phase 2)
  PstMigration.Graph            cert token broker + MockGraphMailboxService
  PstMigration.Infrastructure   EF Core DbContext (SQLite) + DI
  PstMigration.Api              agent endpoints (register/heartbeat/jobs/token)
  PstMigration.Portal           Razor dashboard
  PstMigration.Agent            Windows Service worker (register + heartbeat)
  PstMigration.Tests            xUnit unit tests
```

## Database (key entities)
Tenant, AppRegistration, Agent, AgentHeartbeat, PstFile, PstFolder,
MailboxMapping, MigrationJob, MigrationJobMailbox, MigrationItem,
MigrationAttachment, MigrationError, MigrationAuditLog, GraphRequestLog.

Idempotency: `MigrationItem` has a unique index on
`(JobId, MailboxId, SourceItemId, ItemType)` — re-runs never import twice.
Item statuses: Discovered, Queued, Processing, Completed, CompletedWithWarning,
Skipped, RetryPending, Failed, Cancelled.

## Security
- App-only certificate auth (`ClientCertificateCredential`), scope `.default`.
- Certificate stays on the portal; agents get short-lived tokens only.
- Registration tokens are stored as SHA-256 hashes (`TokenHasher`), never raw.
- No PST/body/attachment content in logs or in the portal DB.
- Least-privilege Graph permissions (see below).

## Entra ID App Registration setup
1. Create an App Registration in the target tenant.
2. Add **application** Graph permissions (admin consent): `Mail.ReadWrite`,
   `Calendars.ReadWrite`, `Contacts.ReadWrite`, `User.Read.All`.
3. Upload a certificate (public key). Keep the private key (PFX) on the **portal**.
4. Configure the portal (per tenant): Tenant Id, Client Id, certificate thumbprint,
   certificate location (`store:CurrentUser/My` or `file:/path/cert.pfx`).
5. Restrict access later with an **Application Access Policy** so the app can only
   write the mailboxes being migrated.

## Build & run
```bash
# requires .NET 8 SDK (the server already has dotnet-sdk-8.0)
cd Migrate/PstMigration
dotnet build PstMigration.sln -c Release
dotnet test  src/PstMigration.Tests/PstMigration.Tests.csproj -c Release

# run the API (agent endpoints + seeds a default tenant)
dotnet run --project src/PstMigration.Api

# run the portal dashboard
dotnet run --project src/PstMigration.Portal

# run the agent (dev console; installed as a Windows Service via MSI later)
dotnet run --project src/PstMigration.Agent
```
Set a real `Agent:RegistrationToken` in `PstMigration.Api/appsettings.json` and the
same value in `PstMigration.Agent/appsettings.json` (`Portal:RegistrationToken`) +
`Portal:Url`.

## Status — Phase 1 (foundation) complete
Done: solution + domain + SQLite DB + agent registration/heartbeat + portal
dashboard + cert token broker + mock PST parser + mock Graph service + tests (13
passing).

Next phases: 2) real PST adapter + inventory · 3) contacts · 4) calendar (safe) ·
5) email best-effort + attachments (upload sessions) · 6) retry/concurrency,
resume/checkpoint, MSI, audit, reports, integration tests.
