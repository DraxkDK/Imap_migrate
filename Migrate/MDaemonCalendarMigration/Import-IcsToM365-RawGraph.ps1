# Import-IcsToM365-RawGraph.ps1
# Imports calendar events from ICS files to Microsoft 365 via raw Graph REST API.
# No Microsoft.Graph module or Install-Module required.
# Uses only built-in PowerShell: Invoke-RestMethod, ConvertFrom-Json, ConvertTo-Json.
#
# Required Entra App Registration permissions:
#   Microsoft Graph > Application permissions > Calendars.ReadWrite > Grant admin consent

param(
    [Parameter(Mandatory)][string]$TenantId,
    [Parameter(Mandatory)][string]$ClientId,
    [Parameter(Mandatory)][string]$ClientSecret,
    [string]$UserMapCsv   = "C:\Migration\user-map.csv",
    [string]$IcsFolder    = "C:\Migration\ICSOutput",
    [string]$ReportCsv    = "C:\Migration\Logs\m365-import-report.csv",
    [string]$M365TimeZone = "SE Asia Standard Time",
    [int]   $DelayMs      = 100,    # pause between Graph POST calls (throttle protection)
    [switch]$WhatIf                 # dry-run: parse + log but do not POST
)

Set-StrictMode -Version 3
$ErrorActionPreference = "Stop"

# ── 1. Acquire access token ──────────────────────────────────────────────────

function Get-GraphAccessToken {
    $tokenUrl = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"
    $body = @{
        client_id     = $ClientId
        scope         = "https://graph.microsoft.com/.default"
        client_secret = $ClientSecret
        grant_type    = "client_credentials"
    }
    $resp = Invoke-RestMethod -Method POST -Uri $tokenUrl `
        -Body $body -ContentType "application/x-www-form-urlencoded"
    return $resp.access_token
}

# ── 2. POST one event JSON to target mailbox ─────────────────────────────────

function Invoke-GraphPostEvent {
    param([string]$AccessToken, [string]$TargetMailbox, [hashtable]$EventBody)
    $uri     = "https://graph.microsoft.com/v1.0/users/$([Uri]::EscapeDataString($TargetMailbox))/events"
    $headers = @{ Authorization = "Bearer $AccessToken" }
    $json    = $EventBody | ConvertTo-Json -Depth 10

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $r = Invoke-RestMethod -Method POST -Uri $uri -Headers $headers `
                -Body $json -ContentType "application/json"
            return $r
        } catch {
            $status = $_.Exception.Response.StatusCode.value__
            if ($status -eq 429 -and $attempt -lt 3) {
                $retry = 5 * $attempt
                Write-Host "  Throttled (429). Waiting ${retry}s…" -ForegroundColor Yellow
                Start-Sleep -Seconds $retry
            } else {
                throw
            }
        }
    }
}

# ── 3. Parse iCalendar date string → ISO 8601 ────────────────────────────────

function ConvertFrom-IcsDate {
    param([string]$Raw, [string]$TzId, [string]$DefaultTz)
    if ([string]::IsNullOrEmpty($Raw)) { return $null }

    $isUtc    = $Raw.EndsWith("Z")
    $isAllDay = -not $Raw.Contains("T")
    $fmt      = if ($isAllDay) { "yyyyMMdd" } elseif ($isUtc) { "yyyyMMddTHHmmssZ" } else { "yyyyMMddTHHmmss" }

    $parsed = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($Raw.TrimEnd("Z"), ($isAllDay ? "yyyyMMdd" : "yyyyMMddTHHmmss"),
            $null, [System.Globalization.DateTimeStyles]::None, [ref]$parsed)) {
        return $null
    }

    $tz = if ($isUtc) { "UTC" } elseif ($TzId) { $TzId } else { $DefaultTz }
    return @{
        dateTime = $parsed.ToString("yyyy-MM-ddTHH:mm:ss")
        timeZone = $tz
    }
}

# ── 4. Extract all VEVENT blocks from ICS content ────────────────────────────

function Get-VEventBlocks([string]$Content) {
    $blocks = @()
    $from   = 0
    while ($true) {
        $s = $Content.IndexOf("BEGIN:VEVENT", $from, [StringComparison]::OrdinalIgnoreCase)
        if ($s -lt 0) { break }
        $e = $Content.IndexOf("END:VEVENT", $s, [StringComparison]::OrdinalIgnoreCase)
        if ($e -lt 0) { break }
        $e += "END:VEVENT".Length
        $blocks += $Content.Substring($s, $e - $s)
        $from    = $e
    }
    return $blocks
}

# ── 5. Parse one VEVENT block → Graph event hashtable ────────────────────────

function ConvertTo-GraphEvent([string]$VEvent) {
    # Unfold RFC 5545 line continuations
    $unfolded = [System.Text.RegularExpressions.Regex]::Replace(
        $VEvent.Replace("`r`n", "`n").Replace("`r", "`n"), "`n[ `t]", "")

    $props = @{}
    foreach ($line in $unfolded -split "`n") {
        if ($line -match "^(BEGIN|END):") { continue }
        $colon = $line.IndexOf(":")
        if ($colon -le 0) { continue }
        $nameAndParams = $line.Substring(0, $colon)
        $value         = $line.Substring($colon + 1)
        $parts         = $nameAndParams -split ";"
        $propName      = $parts[0].ToUpper()
        $tzid          = ($parts | Where-Object { $_ -match "^TZID=(.+)$" } | Select-Object -First 1) -replace "^TZID=", ""
        $props[$propName] = @{ value = $value; tzid = $tzid }
    }

    # --- DTSTART ---
    $dtStartRaw = $props["DTSTART"]?.value
    $dtStartTz  = $props["DTSTART"]?.tzid
    $isAllDay   = $dtStartRaw -and -not $dtStartRaw.Contains("T")

    $startDt = ConvertFrom-IcsDate -Raw $dtStartRaw -TzId $dtStartTz -DefaultTz $M365TimeZone
    if (-not $startDt) { return $null }   # unparseable — skip

    $dtEndRaw  = $props["DTEND"]?.value ?? $props["DUE"]?.value
    $dtEndTz   = $props["DTEND"]?.tzid  ?? $props["DUE"]?.tzid
    $endDt     = ConvertFrom-IcsDate -Raw $dtEndRaw -TzId $dtEndTz -DefaultTz $M365TimeZone
    if (-not $endDt) {
        # derive: all-day +1 day, timed +1 hour
        $base   = [datetime]::ParseExact($startDt.dateTime, "yyyy-MM-ddTHH:mm:ss", $null)
        $endDt  = @{
            dateTime = ($isAllDay ? $base.AddDays(1) : $base.AddHours(1)).ToString("yyyy-MM-ddTHH:mm:ss")
            timeZone = $startDt.timeZone
        }
    }

    $summary     = ($props["SUMMARY"]?.value  ?? "(No Title)") -replace "\\n","`n" -replace "\\,","," -replace "\\;",";"
    $description = ($props["DESCRIPTION"]?.value ?? "")         -replace "\\n","`n" -replace "\\,","," -replace "\\;",";"
    $location    = $props["LOCATION"]?.value

    $showAs = if ($props["TRANSP"]?.value -eq "TRANSPARENT") { "free" } else { "busy" }

    $ev = @{
        subject  = $summary
        body     = @{ contentType = "text"; content = $description }
        start    = $startDt
        end      = $endDt
        isAllDay = $isAllDay
        showAs   = $showAs
    }
    if ($location) { $ev["location"] = @{ displayName = $location } }
    return $ev
}

# ── MAIN ─────────────────────────────────────────────────────────────────────

Write-Host "MDaemon Calendar Migration — ICS → Microsoft 365 (raw Graph REST)"
Write-Host "WhatIf: $WhatIf"
Write-Host ""

# Load user map
$userMap = @{}
Import-Csv $UserMapCsv | ForEach-Object {
    $userMap[$_.SourceEmail.ToLower()] = $_.TargetMailbox
}
Write-Host "Loaded $($userMap.Count) mapping(s) from $UserMapCsv"

# Acquire token
$token = Get-GraphAccessToken
Write-Host "Token acquired.`n"

# Report rows
$report = [System.Collections.Generic.List[PSCustomObject]]::new()

# Process each ICS file
$icsFiles = Get-ChildItem -Path $IcsFolder -Filter "*.ics" -File
foreach ($icsFile in $icsFiles) {
    $sourceEmail  = $icsFile.BaseName.ToLower()
    $targetMailbox = $userMap[$sourceEmail]

    if (-not $targetMailbox) {
        Write-Host "SKIP $sourceEmail — no mapping" -ForegroundColor DarkGray
        $report.Add([PSCustomObject]@{
            SourceEmail = $sourceEmail; TargetMailbox = ""; IcsFile = $icsFile.Name
            TotalEvents = 0; Imported = 0; Failed = 0
            Status = "Skipped"; Message = "No mapping"
        })
        continue
    }

    Write-Host "Processing $sourceEmail → $targetMailbox"
    $content = Get-Content $icsFile.FullName -Raw -Encoding UTF8
    $blocks  = Get-VEventBlocks -Content $content
    Write-Host "  Found $($blocks.Count) VEVENT block(s)"

    $imported = 0; $failed = 0

    foreach ($block in $blocks) {
        $graphEvent = ConvertTo-GraphEvent -VEvent $block
        if (-not $graphEvent) {
            Write-Host "  ⚠ Skipped event (unparseable DTSTART)" -ForegroundColor Yellow
            $failed++; continue
        }

        if ($WhatIf) {
            Write-Host "  [WhatIf] Would import: $($graphEvent.subject)" -ForegroundColor Cyan
            $imported++
        } else {
            try {
                Invoke-GraphPostEvent -AccessToken $token -TargetMailbox $targetMailbox -EventBody $graphEvent | Out-Null
                $imported++
            } catch {
                Write-Host "  ⚠ Failed: $_" -ForegroundColor Red
                $failed++
            }
            Start-Sleep -Milliseconds $DelayMs
        }
    }

    $status = if ($failed -eq 0 -and $imported -gt 0) { "Imported" }
              elseif ($imported -eq 0)                 { "Failed"   }
              else                                      { "Partial"  }
    Write-Host "  → $imported imported, $failed failed ($status)"

    $report.Add([PSCustomObject]@{
        SourceEmail = $sourceEmail; TargetMailbox = $targetMailbox; IcsFile = $icsFile.Name
        TotalEvents = $blocks.Count; Imported = $imported; Failed = $failed
        Status = $status; Message = "$imported imported, $failed failed"
    })
}

# Write report
$report | Export-Csv -Path $ReportCsv -NoTypeInformation -Encoding UTF8
Write-Host "`nImport report: $ReportCsv"
Write-Host "Done."
