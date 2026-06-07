# MDaemon Calendar Extractor GUI

Windows Forms desktop application — .NET 8 — extract iCalendar events from
MDaemon `Calendar.IMAP` folders and export one `.ics` file per source mailbox.

---

## Prerequisites

Install **.NET 8 SDK** (Windows x64):
<https://dotnet.microsoft.com/en-us/download/dotnet/8.0>

Verify after install:
```
dotnet --version
```
Should print `8.0.x`.

---

## Build

```cmd
cd MDaemonCalendarExtractor
dotnet build -c Release
```

Output: `bin\Release\net8.0-windows\MDaemonCalendarExtractor.exe`

### Publish as a self-contained single .exe (recommended for deployment)

```cmd
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\
```

Copy `publish\MDaemonCalendarExtractor.exe` to the target server and run it directly
— no .NET runtime required on the target.

---

## Test before touching production

```cmd
robocopy "C:\MDaemon\Users\old-domain.com\user1" "C:\TestStaging\old-domain.com\user1\Calendar.IMAP" /E
robocopy "C:\MDaemon\Users\old-domain.com\user2" "C:\TestStaging\old-domain.com\user2\Calendar.IMAP" /E
```

Then set **Source Folder** = `C:\TestStaging` in the app.

---

## Usage

| Field | Example |
|---|---|
| Source Folder | `C:\MDaemon\Users` |
| Mapping CSV | `C:\Migration\user-map.csv` |
| Output ICS Folder | `C:\Migration\ICSOutput` |
| Report Folder | `C:\Migration\Logs` |

### CSV format

```csv
SourceEmail,TargetMailbox
user1@old-domain.com,user1@new-domain.com
user2@old-domain.com,user2@new-domain.com
```

### Workflow

1. Set all four paths → **Scan**
2. Review the grid — mapped users are pre-selected
3. **Export Selected** or **Export All Mapped**
4. Open `Logs\convert-report.csv` for the summary

---

## Output file naming

- Files are named after `SourceEmail`, not `TargetMailbox`.
- `@` and `.` are kept intact; only Windows-invalid characters (`\ / : * ? " < > |`) are replaced with `_`.
- Example: `user1@old-domain.com.ics`
- If the file already exists, the app appends a counter: `user1@old-domain.com_2.ics`

---

## Status codes

| Status | Meaning |
|---|---|
| Ready | Scanned, not yet exported |
| Exported | ICS file written successfully |
| NoData | No `BEGIN:VEVENT` found in any calendar file |
| Skipped | No mapping in the CSV |
| Failed | I/O or parse error — check Message column |

---

## Parser behaviour

- Reads `*.mrk`, `*.msg`, `*.ics`, `*.txt` inside each `Calendar.IMAP` folder (recursive).
- Encoding: tries UTF-8 first, falls back to Windows-1252 (ANSI).
- Quoted-printable: decoded automatically when `Content-Transfer-Encoding: quoted-printable` header is present.
- Deduplication: by `UID + DTSTART`; if no UID, by SHA-256 hash of the block.
- Retains `VTIMEZONE` blocks (one per TZID).
- Output uses CRLF line endings as required by RFC 5545.

---

## Limitations

- **Only works if files contain raw iCalendar text.**
  If MDaemon stores calendar data in a proprietary binary format without
  `BEGIN:VEVENT` markers, this tool cannot extract them.
- Recurring events, timezone offsets, attendee lists, and organizer metadata
  are passed through as-is; validate with a pilot user before bulk migration.
- This tool performs **read-only** access to the source folder.
  It never modifies, deletes, or moves source data.
- This tool does **not** import into Microsoft 365 or any other system.
  It only produces `.ics` files (Phase 1 export).

---

## Project structure

```
MDaemonCalendarExtractor/
├── MDaemonCalendarExtractor.csproj
├── Program.cs
├── MainForm.cs               — UI, scan & export orchestration
├── CalendarScanner.cs        — walks Users\ tree, discovers Calendar.IMAP
├── CalendarExtractor.cs      — parses files, extracts VEVENT/VTIMEZONE
├── IcsBuilder.cs             — assembles valid VCALENDAR output
├── CsvUserMapLoader.cs       — loads SourceEmail→TargetMailbox CSV
├── ReportWriter.cs           — writes convert-report.csv
└── Models/
    ├── UserCalendarItem.cs   — grid row model
    └── ReportRow.cs          — CSV report row model
```

Settings are saved to `appsettings.json` next to the `.exe` on each scan.
