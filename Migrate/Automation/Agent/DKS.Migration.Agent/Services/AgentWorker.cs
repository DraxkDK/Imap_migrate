using DKS.Migration.Agent.Models;

namespace DKS.Migration.Agent.Services;

public class AgentWorker : BackgroundService
{
    private readonly AgentConfig _config;
    private readonly PortalClient _portal;
    private readonly OutlookDetector _outlookDetector;
    private readonly PstExporter _pstExporter;
    private readonly ProfileReconfigurer _profileReconfigurer;
    private readonly PstImporter _pstImporter;
    private readonly ILogger<AgentWorker> _logger;

    private DeviceState? _state;
    private bool _migrationComplete;

    public AgentWorker(AgentConfig config, PortalClient portal, OutlookDetector outlookDetector,
        PstExporter pstExporter, ProfileReconfigurer profileReconfigurer,
        PstImporter pstImporter, ILogger<AgentWorker> logger)
    {
        _config = config;
        _portal = portal;
        _outlookDetector = outlookDetector;
        _pstExporter = pstExporter;
        _profileReconfigurer = profileReconfigurer;
        _pstImporter = pstImporter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("DKS Profile Migration Agent starting...");

        // Step 1: Register device with portal
        await RegisterWithPortalAsync(ct);
        if (_state == null)
        {
            _logger.LogError("Registration failed. Agent cannot continue.");
            return;
        }

        // Initial scan runs immediately after registration
        await RunMigrationStepAsync(ct);

        // Main loop: check-in, receive commands, continue migration
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_state != null)
                {
                    var command = await _portal.CheckInAsync(_state.DeviceId, _state.CurrentStep);
                    if (!string.IsNullOrEmpty(command))
                        await ExecuteCommandAsync(command, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in agent worker");
                await _portal.LogAsync(_state?.DeviceId ?? 0, "WorkerLoop", "Error", ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(_config.CheckInIntervalSeconds), ct);
        }

        _logger.LogInformation("Agent worker stopped.");
    }

    private async Task RegisterWithPortalAsync(CancellationToken ct)
    {
        var computerName = Environment.MachineName;
        var windowsUser = Environment.UserName;
        var osVersion = Environment.OSVersion.VersionString;

        _logger.LogInformation("Registering device: {Computer} / {User}", computerName, windowsUser);
        await Log(0, "Register", "Info", $"Agent starting on {computerName} ({windowsUser})");

        _state = await _portal.RegisterAsync(computerName, windowsUser, osVersion);
        if (_state == null) return;

        _logger.LogInformation("Registered. DeviceId={Id}, BatchId={Batch}", _state.DeviceId, _state.BatchId);
        await Log(_state.DeviceId, "Register", "Info", "Device registered successfully.");
    }

    private async Task RunMigrationStepAsync(CancellationToken ct)
    {
        if (_state == null) return;

        switch (_state.CurrentStep)
        {
            case "Pending":
            case "AgentInstalled":
            case "AgentOnline":
                await StepScanOutlookAsync(ct);
                break;

            case "ScanningOutlook":
            case "PstFound":
                if (_config.ShouldExport)
                    await StepExportPstAsync(ct);
                else
                    _state.CurrentStep = "PstExported";
                break;

            case "PstExported":
                if (_config.ShouldReconfig)
                    await StepReconfigureProfileAsync(ct);
                else
                    _state.CurrentStep = "ProfileReconfigured";
                break;

            case "ProfileReconfigured":
                if (_config.ShouldImport && _state.PstFiles.Any())
                    await StepImportPstAsync(ct);
                else
                {
                    _state.CurrentStep = "Completed";
                    _migrationComplete = true;
                    await Log(_state.DeviceId, "Complete", "Info", "Migration completed (no import step).");
                }
                break;

            case "Completed":
                _migrationComplete = true;
                break;
        }
    }

    private async Task StepScanOutlookAsync(CancellationToken ct)
    {
        _state!.CurrentStep = "ScanningOutlook";
        await Log(_state.DeviceId, "ScanOutlook", "Info", "Scanning Outlook installation and profiles...");
        await _portal.CheckInAsync(_state.DeviceId, "ScanningOutlook");

        var outlookVersion = _outlookDetector.DetectOutlookVersion();
        if (outlookVersion == null)
        {
            await Log(_state.DeviceId, "ScanOutlook", "Error", "Outlook not found on this machine.");
            _state.CurrentStep = "Failed";
            return;
        }

        var profiles = _outlookDetector.DetectProfiles();
        var defaultProfile = _outlookDetector.GetDefaultProfile();

        _state.Profiles = profiles;

        // Log profiles tìm được
        foreach (var p in profiles)
        {
            await Log(_state.DeviceId, "ScanOutlook", "Info",
                $"Profile '{p.ProfileName}': {p.Accounts.Count} account(s)" +
                (p.Accounts.Any() ? " — " + string.Join(", ", p.Accounts.Select(a => $"{a.EmailAddress}({a.AccountType})")) : ""));
        }

        // Tất cả POP3 accounts tìm được trên máy
        var allPop3 = profiles.SelectMany(p => p.Accounts).Where(a => a.IsPop3).ToList();

        Models.OutlookAccount? pop3Account = null;

        // Ưu tiên 1: khớp chính xác với oldEmail từ user mapping trên portal
        // → đúng user, đúng máy, đúng account
        if (!string.IsNullOrEmpty(_state.OldEmail))
        {
            pop3Account = allPop3.FirstOrDefault(a =>
                a.EmailAddress.Equals(_state.OldEmail, StringComparison.OrdinalIgnoreCase));

            if (pop3Account != null)
                await Log(_state.DeviceId, "ScanOutlook", "Info",
                    $"Matched POP3 account from user mapping: {pop3Account.EmailAddress}");
            else
                await Log(_state.DeviceId, "ScanOutlook", "Warning",
                    $"User mapping says oldEmail='{_state.OldEmail}' but not found in Outlook scan. " +
                    $"Found POP3: [{string.Join(", ", allPop3.Select(a => a.EmailAddress))}]");
        }

        // Ưu tiên 2: chỉ có 1 POP3 trên máy → lấy luôn
        if (pop3Account == null && allPop3.Count == 1)
        {
            pop3Account = allPop3[0];
            await Log(_state.DeviceId, "ScanOutlook", "Info",
                $"Single POP3 account found: {pop3Account.EmailAddress} — using it.");
        }

        // Ưu tiên 3: nhiều POP3, không có mapping → cần mapping để chọn đúng
        if (pop3Account == null && allPop3.Count > 1)
        {
            await Log(_state.DeviceId, "ScanOutlook", "Warning",
                $"Multiple POP3 accounts found ({string.Join(", ", allPop3.Select(a => a.EmailAddress))}) " +
                "but no user mapping to disambiguate. Add user mapping CSV on portal.");
            _state.CurrentStep = "NeedManualAction";
            await _portal.CheckInAsync(_state.DeviceId, "NeedManualAction",
                errorMessage: $"Multiple POP3 found. Add user mapping with OldEmail to select correct account.");
            return;
        }

        // Ưu tiên 4: không tìm thấy gì trong registry → dùng oldEmail từ mapping (trust admin)
        if (pop3Account == null && !string.IsNullOrEmpty(_state.OldEmail))
        {
            pop3Account = new Models.OutlookAccount
            {
                AccountName = _state.OldEmail,
                EmailAddress = _state.OldEmail,
                AccountType = "POP3"
            };
            await Log(_state.DeviceId, "ScanOutlook", "Info",
                $"No POP3 in registry scan — trusting portal mapping: {_state.OldEmail}");
        }

        // TestMode fallback
        if (pop3Account == null && _config.TestMode)
        {
            pop3Account = new Models.OutlookAccount
            {
                AccountName = _config.TestEmail,
                EmailAddress = _config.TestEmail,
                AccountType = "POP3"
            };
            await Log(_state.DeviceId, "ScanOutlook", "Warning", $"TestMode — fake account: {_config.TestEmail}");
        }

        if (pop3Account == null)
        {
            await Log(_state.DeviceId, "ScanOutlook", "Warning",
                "No POP3 account found and no user mapping. Add user mapping CSV on portal.");
            _state.CurrentStep = "NeedManualAction";
            await _portal.CheckInAsync(_state.DeviceId, "NeedManualAction",
                errorMessage: "No POP3 found. Add user mapping CSV on portal.");
            return;
        }

        // Tìm profile chứa account này
        var ownerProfile = profiles.FirstOrDefault(p =>
            p.Accounts.Any(a => a.EmailAddress.Equals(pop3Account.EmailAddress, StringComparison.OrdinalIgnoreCase)));
        var profileLabel = ownerProfile?.ProfileName ?? "portal-mapping";

        await Log(_state.DeviceId, "ScanOutlook", "Info",
            $"POP3 account: {pop3Account.EmailAddress} (profile: {profileLabel})");

        // Ưu tiên PST path lấy trực tiếp từ registry account entry
        List<string> pop3PstPaths;
        if (!string.IsNullOrEmpty(pop3Account.PstPath) && File.Exists(pop3Account.PstPath))
        {
            pop3PstPaths = new List<string> { pop3Account.PstPath };
            await Log(_state.DeviceId, "ScanOutlook", "Info", $"PST from registry: {pop3Account.PstPath}");
        }
        else
        {
            // Fallback: scan filesystem, lọc theo profile chứa POP3 account này
            var profileNames = ownerProfile != null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ownerProfile.ProfileName }
                : profiles.Where(p => p.Accounts.Any(a => a.IsPop3))
                          .Select(p => p.ProfileName)
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var allPstPaths = _outlookDetector.DetectPstPaths();
            pop3PstPaths = _outlookDetector.FilterPstByProfiles(allPstPaths, profileNames);
        }

        _state.PstFiles = pop3PstPaths.Select(p => new PstFileInfo { SourcePath = p }).ToList();

        await _portal.CheckInAsync(_state.DeviceId, "PstFound",
            outlookVersion: outlookVersion,
            currentProfile: defaultProfile,
            oldAccountType: pop3Account.AccountType,
            oldEmail: pop3Account.EmailAddress);

        foreach (var pst in _state.PstFiles)
        {
            var fi = new FileInfo(pst.SourcePath);
            pst.SizeBytes = fi.Exists ? fi.Length : 0;
            await _portal.ReportPstAsync(_state.DeviceId, pst, "Pending", "Pending");
        }

        await Log(_state.DeviceId, "ScanOutlook", "Info",
            $"Outlook: {outlookVersion} | POP3: {pop3Account.EmailAddress} | PSTs: {pop3PstPaths.Count} file(s)");

        _state.CurrentStep = pop3PstPaths.Any() ? "PstFound" : "PstExported";
    }

    private static string LocalBackupPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DKS", "ProfileAgent", "PSTBackup");

    private async Task StepExportPstAsync(CancellationToken ct)
    {
        // Nếu không có backup path → dùng local
        if (string.IsNullOrEmpty(_state!.BackupPstPath))
        {
            _state.BackupPstPath = LocalBackupPath;
            await Log(_state.DeviceId, "ExportPST", "Warning",
                $"No backup path configured — using local: {_state.BackupPstPath}");
        }
        // Nếu là UNC/network path → kiểm tra có reach được không, nếu không → fallback local
        else if (_state.BackupPstPath.StartsWith(@"\\"))
        {
            try
            {
                Directory.CreateDirectory(_state.BackupPstPath);
            }
            catch
            {
                var orig = _state.BackupPstPath;
                _state.BackupPstPath = LocalBackupPath;
                await Log(_state.DeviceId, "ExportPST", "Warning",
                    $"Network path '{orig}' not reachable — falling back to local: {_state.BackupPstPath}");
            }
        }

        await Log(_state.DeviceId, "ExportPST", "Info", "Starting PST export...");
        _state.CurrentStep = "ExportingPst";
        await _portal.CheckInAsync(_state.DeviceId, "ExportingPst");

        if (_outlookDetector.IsOutlookRunning() && _state.CloseOutlookAutomatically)
        {
            await Log(_state.DeviceId, "ExportPST", "Info", "Closing Outlook...");
            _outlookDetector.CloseOutlook();
            await Task.Delay(3000, ct);
        }

        var sourcePaths = _state.PstFiles.Select(p => p.SourcePath).ToList();
        var exported = await _pstExporter.ExportAllAsync(sourcePaths, _state.BackupPstPath,
            Environment.MachineName, Environment.UserName, ct);

        for (int i = 0; i < _state.PstFiles.Count && i < exported.Count; i++)
        {
            _state.PstFiles[i] = exported[i];
            var status = exported[i].IsExported ? "Exported" : "Failed";
            await _portal.ReportPstAsync(_state.DeviceId, exported[i], status, "Pending");
        }

        var anyFailed = exported.Any(p => !p.IsExported);
        if (anyFailed)
        {
            await Log(_state.DeviceId, "ExportPST", "Warning", "Some PSTs failed to export.");
        }

        await Log(_state.DeviceId, "ExportPST", "Info", $"PST export done. {exported.Count(p => p.IsExported)}/{exported.Count} exported.");
        _state.CurrentStep = "PstExported";
        await _portal.CheckInAsync(_state.DeviceId, "PstExported");
    }

    private async Task StepReconfigureProfileAsync(CancellationToken ct)
    {
        await Log(_state!.DeviceId, "ReconfigProfile", "Info", "Starting profile reconfiguration...");
        _state.CurrentStep = "ReconfiguringProfile";
        await _portal.CheckInAsync(_state.DeviceId, "ReconfiguringProfile");

        var backupPath = Path.Combine(_state.BackupPstPath ?? LocalBackupPath, "ProfileBackup");
        var currentProfile = _outlookDetector.GetDefaultProfile();

        // Backup profile hiện tại
        if (!string.IsNullOrEmpty(currentProfile))
        {
            _profileReconfigurer.BackupProfile(currentProfile, backupPath);
            await Log(_state.DeviceId, "ReconfigProfile", "Info", $"Profile '{currentProfile}' backed up to {backupPath}");
        }

        // Tìm profile Exchange/M365 đang có sẵn
        var exchangeProfile = _profileReconfigurer.FindExchangeProfile();

        // Xác định email POP3 cần disable
        var pop3Email = _state.OldEmail
                     ?? _state.Profiles.SelectMany(p => p.Accounts)
                                       .FirstOrDefault(a => a.IsPop3)?.EmailAddress;

        if (!string.IsNullOrEmpty(exchangeProfile))
        {
            // Có Exchange profile → disable POP3 account khỏi profile đó, set làm default
            if (!string.IsNullOrEmpty(pop3Email))
            {
                var removed = _profileReconfigurer.RemovePop3Account(exchangeProfile, pop3Email);
                await Log(_state.DeviceId, "ReconfigProfile", "Info",
                    removed
                        ? $"POP3 account '{pop3Email}' disabled from profile '{exchangeProfile}'"
                        : $"Could not disable POP3 account — may need manual removal");
            }

            _profileReconfigurer.SetDefaultProfile(exchangeProfile);
            await Log(_state.DeviceId, "ReconfigProfile", "Info",
                $"Exchange/M365 profile '{exchangeProfile}' set as default.");
        }
        else
        {
            // Không có Exchange profile → user cần tự tạo tài khoản M365
            await Log(_state.DeviceId, "ReconfigProfile", "Warning",
                "No Exchange/M365 profile found. User must add M365 account manually in Outlook.");
        }

        var targetMailbox = _state.TargetMailbox ?? _state.NewEmail;

        if (!string.IsNullOrEmpty(targetMailbox))
        {
            // Tạo profile mới "Microsoft 365" bằng PRF — clean, không đụng profile cũ
            var newProfileName = "Microsoft 365";
            var ok = _profileReconfigurer.AddM365AccountViaPrf(newProfileName, targetMailbox, overwrite: true);
            if (ok)
            {
                await Log(_state.DeviceId, "ReconfigProfile", "Info",
                    $"New profile '{newProfileName}' created for '{targetMailbox}'. Outlook will open — user must sign in with Modern Auth.");
            }
            else
            {
                await Log(_state.DeviceId, "ReconfigProfile", "Warning",
                    "PRF import failed — launching Outlook manually for user to set up account.");
                _profileReconfigurer.LaunchOutlookForSignIn();
            }
        }
        else
        {
            await Log(_state.DeviceId, "ReconfigProfile", "Warning",
                "No target mailbox — add user mapping on portal then retry. Launching Outlook for manual setup.");
            _profileReconfigurer.LaunchOutlookForSignIn();
        }

        _state.CurrentStep = "ProfileReconfigured";
        await _portal.CheckInAsync(_state.DeviceId, "ProfileReconfigured");
    }

    private async Task StepImportPstAsync(CancellationToken ct)
    {
        await Log(_state!.DeviceId, "ImportPST", "Info", "Starting PST import...");
        _state.CurrentStep = "ImportingPst";
        await _portal.CheckInAsync(_state.DeviceId, "ImportingPst");

        var newProfileName = _outlookDetector.GetDefaultProfile();

        foreach (var pst in _state.PstFiles.Where(p => p.IsExported))
        {
            ct.ThrowIfCancellationRequested();
            var pstPath = pst.BackupPath ?? pst.SourcePath;

            await Log(_state.DeviceId, "ImportPST", "Info", $"Importing {Path.GetFileName(pstPath)}...");

            bool ok;
            if (!string.IsNullOrEmpty(_state.ImportTargetFolder))
                ok = await _pstImporter.ImportPstIntoFolderAsync(pstPath, newProfileName, _state.ImportTargetFolder, ct);
            else
                ok = await _pstImporter.AttachPstAsync(pstPath, newProfileName, ct);

            pst.IsImported = ok;
            await _portal.ReportPstAsync(_state.DeviceId, pst, "Exported", ok ? "Imported" : "Failed");
            await Log(_state.DeviceId, "ImportPST", ok ? "Info" : "Error",
                $"PST {Path.GetFileName(pstPath)}: {(ok ? "imported OK" : "import failed")}");
        }

        _state.CurrentStep = "Completed";
        _migrationComplete = true;
        await _portal.CheckInAsync(_state.DeviceId, "Completed");
        await Log(_state.DeviceId, "Complete", "Info", "Migration completed successfully!");
    }

    private async Task ExecuteCommandAsync(string command, CancellationToken ct)
    {
        await Log(_state!.DeviceId, "Command", "Info", $"Executing portal command: {command}");
        switch (command)
        {
            case "ScanOutlook":
                _state.CurrentStep = "AgentOnline";
                await StepScanOutlookAsync(ct);
                break;
            case "ExportPst":
                _state.CurrentStep = "PstFound";
                await StepExportPstAsync(ct);
                break;
            case "ReconfigureProfile":
                _state.CurrentStep = "PstExported";
                await StepReconfigureProfileAsync(ct);
                break;
            case "ImportPst":
                _state.CurrentStep = "ProfileReconfigured";
                await StepImportPstAsync(ct);
                break;
            case "RetryFailed":
                _migrationComplete = false;
                _state.CurrentStep = "AgentOnline";
                await StepScanOutlookAsync(ct);
                break;
            case "Rollback":
                var oldProfile = _outlookDetector.GetDefaultProfile();
                _profileReconfigurer.RollbackToProfile(oldProfile);
                _state.CurrentStep = "NeedManualAction";
                await Log(_state.DeviceId, "Rollback", "Warning", "Rollback executed from portal command.");
                break;
            case "Uninstall":
                await UninstallSelfAsync();
                break;
            default:
                await Log(_state.DeviceId, "Command", "Warning", $"Unknown command: {command}");
                break;
        }
    }

    private async Task UninstallSelfAsync()
    {
        await Log(_state!.DeviceId, "Uninstall", "Warning", "Uninstall requested from portal — removing agent...");

        string? productCode = null;
        try
        {
            using var reg = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\DKS\ProfileAgent");
            productCode = reg?.GetValue("ProductCode") as string;
        }
        catch { /* ignore */ }

        // Remove the logon auto-start entry so it cannot relaunch.
        try
        {
            using var run = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue("DKSProfileAgent", throwOnMissingValue: false);
        }
        catch { /* ignore */ }

        if (string.IsNullOrEmpty(productCode))
        {
            await Log(_state.DeviceId, "Uninstall", "Error",
                "ProductCode not found in registry — cannot self-uninstall. Reinstall with the current MSI, or uninstall manually.");
            return;
        }

        try
        {
            await _portal.CheckInAsync(_state.DeviceId, _state.CurrentStep,
                errorMessage: "Agent uninstalling (portal command).");
        }
        catch { /* best effort */ }

        // Delay so this process exits and unlocks its files before msiexec removes them.
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c timeout /t 4 /nobreak & msiexec /x {productCode} /qn")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        System.Diagnostics.Process.Start(psi);

        await Log(_state.DeviceId, "Uninstall", "Info", "msiexec /x launched. Agent shutting down.");
        Environment.Exit(0);
    }

    private Task Log(int deviceId, string step, string level, string message)
    {
        _logger.LogInformation("[{Step}] {Message}", step, message);
        return deviceId > 0 ? _portal.LogAsync(deviceId, step, level, message) : Task.CompletedTask;
    }
}
