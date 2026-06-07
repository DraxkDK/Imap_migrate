=========================================================
MDaemon Calendar Migration Tool
.NET 8 Windows Forms — Portable Click-to-Run
=========================================================

REQUIREMENTS
  .NET 8 SDK for building:
  https://dotnet.microsoft.com/en-us/download/dotnet/8.0

  Target machine (after publish): NO .NET runtime needed.

---------------------------------------------------------
BUILD (developer machine)
---------------------------------------------------------

  dotnet build -c Release

  Output: bin\Release\net8.0-windows\MDaemonCalendarMigration.exe

---------------------------------------------------------
PUBLISH — single self-contained .exe (no runtime on target)
---------------------------------------------------------

  dotnet publish -c Release -r win-x64 ^
    --self-contained true ^
    /p:PublishSingleFile=true ^
    /p:PublishTrimmed=false ^
    /p:EnableCompressionInSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    -o ".\publish\win-x64"

  Copy to target:
    publish\win-x64\MDaemonCalendarMigration.exe
    publish\win-x64\appsettings.json       (template — app writes to this)
    sample-user-map.csv
    README.txt

---------------------------------------------------------
QUICK START — TEST BEFORE PRODUCTION
---------------------------------------------------------

  1. Copy 2-3 test users to staging:
       robocopy "C:\MDaemon\Users\old-domain.com\user1" ^
           "C:\TestUsers\old-domain.com\user1\Calendar.IMAP" /E

  2. Run MDaemonCalendarMigration.exe

  3. Tab 1 — Extract ICS:
       Source Folder  = C:\TestUsers
       Mapping CSV    = path\to\user-map.csv
       Output Folder  = C:\Migration\ICSOutput
       Report Folder  = C:\Migration\Logs
       [Scan] → [Export Selected]

  4. Tab 2 — Import to M365:
       Tenant ID      = your-tenant-id
       Client ID      = your-app-client-id
       Client Secret  = your-app-secret  (click [Save] to encrypt)
       Time Zone      = SE Asia Standard Time
       ICS Folder     = C:\Migration\ICSOutput   (auto-filled from Tab 1)
       Mapping CSV    = same CSV
       [Test Connection] → [Load ICS Files] → [Import Selected]

  5. Tab 3 — Reports: view summary and open CSV reports.

---------------------------------------------------------
ENTRA APP REGISTRATION
---------------------------------------------------------

  1. Azure Portal → Entra ID → App registrations → New registration
  2. Note: Application (client) ID, Directory (tenant) ID
  3. Certificates & secrets → New client secret → copy value
  4. API permissions → Add permission → Microsoft Graph
     → Application permissions → Calendars.ReadWrite
  5. Grant admin consent

  No delegated/interactive login. App-only (client credentials).
  No Microsoft.Graph PowerShell module or Install-Module required.

---------------------------------------------------------
CSV FORMAT
---------------------------------------------------------

  SourceEmail,TargetMailbox
  user1@old-domain.com,user1@new-domain.com

---------------------------------------------------------
OUTPUT FILE NAMING
---------------------------------------------------------

  SourceEmail = user1@old-domain.com
  File        = user1@old-domain.com.ics    (@ and . kept intact)

  Conflict    = user1@old-domain.com_2.ics

---------------------------------------------------------
REPORTS
---------------------------------------------------------

  extract-report.csv  — one row per user (Tab 1 result)
  import-report.csv   — one row per ICS file (Tab 2 result)

---------------------------------------------------------
PARSER BEHAVIOUR
---------------------------------------------------------

  - Reads *.mrk, *.msg, *.ics, *.txt inside Calendar.IMAP (recursive)
  - Encoding: UTF-8, fallback Windows-1252
  - Quoted-Printable: decoded when header present
  - CRLF normalised per RFC 5545
  - Dedup: UID+DTSTART; fallback SHA-256 hash
  - VTIMEZONE blocks preserved (one per TZID)

---------------------------------------------------------
LIMITATIONS
---------------------------------------------------------

  - Only works if files contain raw iCalendar VEVENT blocks.
  - If MDaemon stores calendar in proprietary binary format → NoData.
  - Recurring events (RRULE) are passed through as-is; validate
    with pilot user before bulk migration.
  - M365 import rate: ~1 event per 100ms. Large calendars take time.
  - This tool performs READ-ONLY access to the source MDaemon folder.
  - Does NOT use Microsoft.Graph PowerShell module.
  - Does NOT use delegated (user) login.

=========================================================
