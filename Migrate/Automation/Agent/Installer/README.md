# Build the DKS Profile Agent MSI (WiX v5)

The MSI installs the agent to *Program Files*, writes its config to
`HKLM\SOFTWARE\DKS\ProfileAgent` from the msiexec properties, and launches it at
each user logon (Run key). The agent runs in the **interactive user session**
(it automates the user's Outlook), so it is **not** a LocalSystem service.

## Prerequisites
- .NET 8 SDK (already on the server: `dotnet --version`)
- WiX v5 CLI tool:
  ```bash
  dotnet tool install --global wix --version 5.0.2
  export PATH="$PATH:$HOME/.dotnet/tools"     # add to ~/.bashrc to persist
  wix --version
  ```

## Build (run on the Linux server — WiX v5 builds MSIs cross-platform)

```bash
cd /opt/imap-migrate/Migrate/Automation/Agent

# 1. Publish the agent (self-contained so target PCs don't need .NET installed)
dotnet publish DKS.Migration.Agent/DKS.Migration.Agent.csproj \
  -c Release -r win-x64 --self-contained true \
  -o Installer/publish

# 2. Build the MSI from the published files
cd Installer
wix build Package.wxs -arch x64 -o DKSProfileAgent.msi

# 3. Publish it to the portal so the Download button serves it
cp DKSProfileAgent.msi /opt/dks-portal/wwwroot/agent/DKSProfileAgent.msi
```

> Alternative: `dotnet build DKSProfileAgent.Installer.wixproj -c Release` (uses
> the WiX MSBuild SDK) instead of the `wix build` CLI — either produces the MSI.

## Configuration the MSI accepts (properties)

| Property | Maps to registry value | Default |
|---|---|---|
| `APIURL` | ApiUrl | https://migrate.draxk.com/api/agent |
| `CUSTOMERCODE` | CustomerCode | (empty) |
| `AGENTTOKEN` | AgentToken | (empty) |
| `MODE` | Mode | EXPORT_RECONFIG_IMPORT |
| `LOGPATH` | LogPath | C:\ProgramData\DKS\ProfileAgent\Logs |
| `AUTOSTART` | (Run key, only if "true") | true |

The agent reads these from the registry at startup (see `Agent/Program.cs`).
Precedence: appsettings.json < registry < environment variables.

## Test on a Windows PC (with Outlook)

```bat
msiexec /i DKSProfileAgent.msi /qn ^
  APIURL="https://migrate.draxk.com/api/agent" ^
  CUSTOMERCODE="ACME" ^
  AGENTTOKEN="<batch token from the portal>" ^
  MODE="EXPORT_RECONFIG_IMPORT" ^
  AUTOSTART="true"
```

- Installs to `C:\Program Files\DKS Profile Agent\`.
- Runs at next logon; to start immediately, run
  `"C:\Program Files\DKS Profile Agent\DKSProfileAgent.exe"`.
- The device should then appear under **Devices** in the portal.
- Uninstall: `msiexec /x DKSProfileAgent.msi /qn` (or via Apps & features).

## Mass deployment
Use the **GPO** / **Intune** scripts generated on the portal's Agent Installer
page — they call `msiexec` with the right properties for the selected batch.
