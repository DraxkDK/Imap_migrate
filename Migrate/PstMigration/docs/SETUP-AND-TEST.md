# First end-to-end test (App Registration + smoke test)

Everything is compile-verified only. This runbook gets one PST migrated to one
test mailbox so the runtime path (cert auth → Graph → mailbox) is proven.

Use a **test tenant** and a **test mailbox** you can throw away.

## 1. Create the Entra App Registration
1. Entra admin center → App registrations → New registration → name `PstMigration`.
2. Note **Application (client) ID** and **Directory (tenant) ID**.
3. API permissions → Add → Microsoft Graph → **Application permissions**:
   `Mail.ReadWrite`, `Calendars.ReadWrite`, `Contacts.ReadWrite`, `User.Read.All`.
4. Click **Grant admin consent**.

## 2. Choose a credential
**Option A — Client secret (simplest):** App registration → Certificates & secrets
→ New client secret → copy the **Value**. You'll paste it on the portal Tenants
page; it is stored **encrypted at rest** (AES-GCM with `PstMigration:MasterKey`).
Secrets expire (max 24 months). Skip section 2b.

**Option B — Certificate (recommended for production):**
On the machine that runs the **Portal** (dev box or VPS):
```powershell
# Windows (PowerShell) — self-signed cert in CurrentUser\My
$cert = New-SelfSignedCertificate -Subject "CN=PstMigration" -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy Exportable -KeySpec Signature -NotAfter (Get-Date).AddYears(2)
$cert.Thumbprint
Export-Certificate -Cert $cert -FilePath pstmigration.cer   # public key only
```
```bash
# Linux (OpenSSL) — PFX + public cer
openssl req -x509 -newkey rsa:2048 -keyout key.pem -out pstmigration.cer -days 730 -nodes -subj "/CN=PstMigration"
openssl pkcs12 -export -out pstmigration.pfx -inkey key.pem -in pstmigration.cer -passout pass:
```
In the App Registration → **Certificates & secrets → Certificates → Upload** the
`.cer` (public key). Keep the private key (cert store / PFX) only on the portal.

## 3. Scope access (least privilege) — recommended
By default the app can write **every** mailbox. Restrict it to the test mailbox:
```powershell
New-ApplicationAccessPolicy -AppId <client-id> -PolicyScopeGroupId <mail-enabled-security-group> -AccessRight RestrictAccess -Description "PstMigration test"
```
Put the test mailbox in that security group.

## 4. Add the tenant on the Portal (UI)
Run the Portal, open **Tenants**, and **Add tenant**:
- Customer name, Tenant domain
- Directory (tenant) ID, Application (client) ID
- Certificate thumbprint + location (`store:CurrentUser/My` or `file:/path/cert.pfx`)

On save the portal shows a **registration token once** — copy it; that is the
`REGISTRATION_TOKEN` for agents of this customer. Each customer/tenant gets its
own token (multi-tenant). No `appsettings.json` editing or DB deletion needed.

## 5. Run the three components
```bash
# from Migrate/PstMigration  (add DOTNET_ROLL_FORWARD=LatestMajor on a net10-only box)
dotnet run --project src/PstMigration.Api      # agent API + token broker
dotnet run --project src/PstMigration.Portal   # dashboard (http://localhost:5xxx)

# Agent: set Portal:Url + Portal:RegistrationToken in its appsettings.json, then
dotnet run --project src/PstMigration.Agent
```

## 6. Drive the test
1. Put a small `.pst` in the agent's scan folder (Api `Agent:DefaultPstFolders`,
   default `C:\ProgramData\DKS\PST`).
2. Agent registers → scans → PST shows on the Portal **PST Inventory** page.
3. Portal **Mappings** → import a CSV mapping that PST to the test mailbox:
   ```csv
   PstPath,TargetMailbox,DisplayName,EmailMode,CalendarMode,ContactMode
   C:\ProgramData\DKS\PST\test.pst,testuser@yourtenant.onmicrosoft.com,Test,GraphBestEffort,Safe,Enabled
   ```
4. Portal **Mappings → Create migration job**.
5. Agent picks up the job (~30s) → migrates. Watch **Reports** + agent logs.
6. In the test mailbox (OWA): check folder **Imported PST**, **Imported PST
   Contacts**, **Imported PST Calendar**.

## What to watch for (likely first failures)
- **401/403 from Graph** → admin consent missing, or Application Access Policy
  excludes the mailbox, or cert not matched.
- **Token broker error** → cert thumbprint/location wrong on the portal.
- **Contacts have no email / no recurrence** → known XstReader limitation
  (named MAPI props). Switch `IPstParser` to a commercial parser if required.
- **Mail in Drafts-like state / timestamps off** → expected best-effort behavior.

Report the first error + agent log and we iterate from there.
