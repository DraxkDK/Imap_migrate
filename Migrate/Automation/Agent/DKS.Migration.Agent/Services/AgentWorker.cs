using DKS.Migration.Agent.Models;
using System.Net.Mail;

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
    private string? _originalProfileName;
    private string? _newProfileName;
    private DateTime? _signInWaitStart;
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromHours(2);

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
        while (!ct.IsCancellationRequested && !_migrationComplete)
        {
            try
            {
                if (_state != null)
                {
                    // Đang chờ user sign in → poll detect session ready rồi auto-import
                    if (_state.CurrentStep == "WaitingForSignIn")
                        await PollSignInAndImportAsync(ct);

                    var checkIn = await _portal.CheckInAsync(_state.DeviceId, _state.CurrentStep);
                    // Refresh the target mailbox from the portal each cycle so an admin can
                    // set/change it without restarting the agent.
                    if (!string.IsNullOrWhiteSpace(checkIn?.TargetMailbox))
                        _state.TargetMailbox = checkIn.TargetMailbox;
                    if (!string.IsNullOrEmpty(checkIn?.PendingCommand))
                        await ExecuteCommandAsync(checkIn.PendingCommand, ct);
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
            oldEmail: pop3Account.EmailAddress,
            newMailbox: _state.NewEmail ?? _state.TargetMailbox);

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

        // Lưu lại để rollback về đúng profile gốc
        if (!string.IsNullOrEmpty(currentProfile))
            _originalProfileName = currentProfile;

        // Backup profile hiện tại
        if (!string.IsNullOrEmpty(currentProfile))
        {
            _profileReconfigurer.BackupProfile(currentProfile, backupPath);
            await Log(_state.DeviceId, "ReconfigProfile", "Info", $"Profile '{currentProfile}' backed up to {backupPath}");
        }

        var targetMailbox = ResolveM365MailboxUpn();

        if (string.IsNullOrEmpty(targetMailbox))
        {
            await Log(_state.DeviceId, "ReconfigProfile", "Warning",
                "No valid M365 sign-in UPN found. NewEmail/TargetMailbox must be a plain email like user@domain.com. Launching Outlook for manual setup.");
            _profileReconfigurer.LaunchOutlookForSignIn();
            _state.CurrentStep = "NeedManualAction";
            await _portal.CheckInAsync(_state.DeviceId, "NeedManualAction",
                errorMessage: "Set NewEmail or TargetMailbox to the real M365 sign-in email, sign in to Outlook manually, then run Import PST.");
            return;
        }

        // POP3 → M365: luôn tạo profile mới sạch. Profile POP3 cũ giữ nguyên làm
        // rollback (_originalProfileName). Không đụng FindExchangeProfile vì profile
        // "Microsoft 365" broken sót lại từ lần chạy trước sẽ bị CreateEmptyProfile xóa.

        // Đóng Outlook trước khi tạo profile mới
        if (_outlookDetector.IsOutlookRunning())
        {
            await Log(_state.DeviceId, "ReconfigProfile", "Info", "Closing Outlook...");
            _outlookDetector.CloseOutlook();
            await Task.Delay(3000, ct);
        }

        var newProfileName = "Microsoft 365";
        _newProfileName = newProfileName;
        var ok = _profileReconfigurer.CreateEmptyProfileAndLaunch(newProfileName);
        if (ok)
        {
            await Log(_state.DeviceId, "ReconfigProfile", "Info",
                $"Empty profile '{newProfileName}' created. Outlook opened — user signs in once with {targetMailbox}. Agent will auto-import after sign-in.");
            // Chờ user sign in trên popup → agent tự detect session ready rồi auto-import
            _state.CurrentStep = "WaitingForSignIn";
            _signInWaitStart = DateTime.UtcNow;
            await _portal.CheckInAsync(_state.DeviceId, "WaitingForSignIn",
                errorMessage: $"User: sign in to Outlook with {targetMailbox} on the popup, then wait. Import runs automatically.");
        }
        else
        {
            await Log(_state.DeviceId, "ReconfigProfile", "Warning",
                "Could not create profile — launching Outlook for manual setup.");
            _profileReconfigurer.LaunchOutlookForSignIn();
            _state.CurrentStep = "ProfileReconfigured";
            await _portal.CheckInAsync(_state.DeviceId, "ProfileReconfigured");
        }
    }

    // Poll xem user đã sign in xong chưa (Exchange session ready trong profile mới).
    // Khi ready → agent ride trên session đã auth đó để import, không cần password.
    private async Task PollSignInAndImportAsync(CancellationToken ct)
    {
        var profileName = _newProfileName ?? "Microsoft 365";

        if (!_profileReconfigurer.IsExchangeAccountReady(profileName))
        {
            // Timeout: user không sign in trong giới hạn → chuyển manual
            if (_signInWaitStart.HasValue && DateTime.UtcNow - _signInWaitStart.Value > SignInTimeout)
            {
                await Log(_state!.DeviceId, "WaitSignIn", "Warning",
                    $"User did not sign in within {SignInTimeout.TotalMinutes:0} min. Marking NeedManualAction.");
                _state.CurrentStep = "NeedManualAction";
                await _portal.CheckInAsync(_state.DeviceId, "NeedManualAction",
                    errorMessage: "Sign-in timeout. User must sign in to Outlook, then trigger Import PST from portal.");
            }
            else
            {
                await Log(_state!.DeviceId, "WaitSignIn", "Info", "Waiting for user to sign in to Outlook...");
            }
            return;
        }

        // Session ready → tự động import
        await Log(_state!.DeviceId, "WaitSignIn", "Info", "Sign-in detected — Exchange session ready. Starting auto-import.");
        _state.CurrentStep = "ProfileReconfigured";
        await StepImportPstAsync(ct);
    }

    private async Task StepImportPstAsync(CancellationToken ct)
    {
        await Log(_state!.DeviceId, "ImportPST", "Info", "Starting PST import...");
        _state.CurrentStep = "ImportingPst";
        await _portal.CheckInAsync(_state.DeviceId, "ImportingPst");

        var newProfileName = _outlookDetector.GetDefaultProfile();

        // State in-memory có thể rỗng (agent restart / trigger step riêng lẻ qua portal).
        // Quét lại PST đã export từ thư mục backup trên disk để không import vào khoảng không.
        var exportedPsts = _state.PstFiles.Where(p => p.IsExported).ToList();
        if (!exportedPsts.Any())
        {
            var dir = Path.Combine(_state.BackupPstPath ?? LocalBackupPath,
                Environment.MachineName, Environment.UserName);
            if (Directory.Exists(dir))
            {
                exportedPsts = Directory.GetFiles(dir, "*.pst", SearchOption.TopDirectoryOnly)
                    .Select(f => new PstFileInfo { SourcePath = f, BackupPath = f, IsExported = true })
                    .ToList();
                await Log(_state.DeviceId, "ImportPST", "Info",
                    $"State empty — recovered {exportedPsts.Count} exported PST(s) from backup: {dir}");
            }
        }

        if (!exportedPsts.Any())
        {
            await Log(_state.DeviceId, "ImportPST", "Warning",
                "No exported PST found to import. Run Export PST first.");
            _state.CurrentStep = "NeedManualAction";
            await _portal.CheckInAsync(_state.DeviceId, "NeedManualAction",
                errorMessage: "No exported PST found on disk. Run Export PST before Import.");
            return;
        }

        foreach (var pst in exportedPsts)
        {
            ct.ThrowIfCancellationRequested();
            var pstPath = pst.BackupPath ?? pst.SourcePath;

            await Log(_state.DeviceId, "ImportPST", "Info", $"Importing {Path.GetFileName(pstPath)}...");

            // Luôn copy mail VÀO mailbox M365 (merge theo folder name) → sync lên cloud.
            // ImportTargetFolder trống → import vào root mailbox, merge Inbox/Sent... theo tên.
            var ok = await _pstImporter.ImportPstIntoFolderAsync(pstPath, newProfileName, _state.ImportTargetFolder, ct);

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
                var profileToRestore = _originalProfileName ?? _outlookDetector.GetDefaultProfile();
                _profileReconfigurer.RollbackToProfile(profileToRestore);
                _state.CurrentStep = "NeedManualAction";
                await Log(_state.DeviceId, "Rollback", "Warning", $"Rollback executed — restored profile '{profileToRestore}'.");
                break;
            case "ImportToM365":
                await GraphImportAsync(ct);
                break;
            case "Uninstall":
                await UninstallSelfAsync();
                break;
            default:
                await Log(_state.DeviceId, "Command", "Warning", $"Unknown command: {command}");
                break;
        }
    }

    private async Task GraphImportAsync(CancellationToken ct)
    {
        var mailbox = _state!.TargetMailbox ?? _state.NewEmail;
        if (string.IsNullOrWhiteSpace(mailbox))
        {
            await Log(_state.DeviceId, "GraphImport", "Error", "No target mailbox set (add user mapping with NewEmail/TargetMailbox).");
            return;
        }

        await Log(_state.DeviceId, "GraphImport", "Info", $"Requesting Graph token for {mailbox}...");
        var token = await _portal.GetGraphTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            await Log(_state.DeviceId, "GraphImport", "Error", "Could not get Graph token — check the customer's Microsoft 365 app registration on the portal.");
            _state.CurrentStep = "Failed";
            await _portal.CheckInAsync(_state.DeviceId, "Failed", errorMessage: "Graph token unavailable");
            return;
        }

        // Collect PSTs from the backup folder + any reported source paths.
        var psts = new List<string>();
        var backupDir = string.IsNullOrEmpty(_state.BackupPstPath) ? LocalBackupPath : _state.BackupPstPath;
        if (Directory.Exists(backupDir))
            psts.AddRange(Directory.EnumerateFiles(backupDir, "*.pst", SearchOption.AllDirectories));
        foreach (var p in _state.PstFiles)
        {
            var path = p.BackupPath ?? p.SourcePath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path) && !psts.Contains(path))
                psts.Add(path);
        }
        if (psts.Count == 0)
        {
            await Log(_state.DeviceId, "GraphImport", "Warning", "No PST found to import. Run Export PST first.");
            return;
        }

        _state.CurrentStep = "ImportingToM365";
        await _portal.CheckInAsync(_state.DeviceId, "ImportingToM365");
        var rootFolder = string.IsNullOrWhiteSpace(_state.ImportTargetFolder) ? "Imported PST" : _state.ImportTargetFolder!.TrimStart('/');

        int totalImported = 0, totalFailed = 0, totalSkipped = 0;
        using (var importer = new GraphImporter(token, _logger))
        {
            foreach (var pst in psts.Distinct())
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(pst);
                await Log(_state.DeviceId, "GraphImport", "Info", $"Importing {name} -> {mailbox} ...");
                await _portal.ReportProgressAsync(_state.DeviceId, 0, $"{name}: starting…");

                var progress = new Progress<ImportProgress>(p =>
                {
                    _logger.LogInformation("[{Pst}] {Summary}", name, p.Summary());
                    _ = _portal.LogAsync(_state.DeviceId, "GraphImport", "Info", $"{name}: {p.Summary()}");
                    _ = _portal.ReportProgressAsync(_state.DeviceId, (int)p.Percent, $"{name}: {p.Summary()}");
                });

                // Copy the PST to a temp file (prompting the user to close Outlook if it has the PST
                // locked), then import from the copy so the user can reopen Outlook immediately.
                string? tempCopy = null;
                try
                {
                    tempCopy = await CopyWithPromptAsync(pst, name, ct);
                    if (tempCopy == null)
                    {
                        totalFailed++;
                        await Log(_state.DeviceId, "GraphImport", "Error",
                            $"{name}: Outlook vẫn giữ file PST sau khi chờ. Đóng Outlook rồi bấm Import lại.");
                        continue;
                    }

                    var (imp, fail, skip, firstErr) = await importer.ImportPstAsync(tempCopy, mailbox, rootFolder, progress, ct);
                    totalImported += imp; totalFailed += fail; totalSkipped += skip;
                    await Log(_state.DeviceId, "GraphImport", fail > 0 ? "Warning" : "Info",
                        $"{name}: {imp} imported, {skip} skipped (already there), {fail} failed");
                    if (fail > 0 && firstErr != null)
                        await Log(_state.DeviceId, "GraphImport", "Error", $"{name} first failure: {firstErr}");
                }
                catch (Exception ex)
                {
                    totalFailed++;
                    await Log(_state.DeviceId, "GraphImport", "Error", $"{name} failed: {ex.Message}");
                }
                finally
                {
                    if (tempCopy != null)
                        try { File.Delete(tempCopy); } catch { /* best effort */ }
                }
            }
        }

        _state.CurrentStep = totalFailed > 0 ? "CompletedWithWarning" : "Completed";
        await _portal.CheckInAsync(_state.DeviceId, _state.CurrentStep);
        await Log(_state.DeviceId, "GraphImport", "Info", $"Graph import complete: {totalImported} imported, {totalSkipped} skipped, {totalFailed} failed.");
    }

    /// <summary>Copies a PST to a temp file. If Outlook has it locked, shows a popup asking the user
    /// to close Outlook (with an estimated time), waits until it's free, copies, then tells them they
    /// can reopen Outlook. Returns the temp path, or null if it stays locked past the timeout.</summary>
    private async Task<string?> CopyWithPromptAsync(string pst, string name, CancellationToken ct)
    {
        // Fast path: not locked → copy straight away, no popup.
        try { return await CopyPstForReadAsync(pst, ct); }
        catch (IOException) { /* locked by Outlook — prompt + wait below */ }

        long size = 0;
        try { size = new FileInfo(pst).Length; } catch { /* ignore */ }
        var gb = size / 1024.0 / 1024 / 1024;
        var estMin = Math.Max(1, (int)Math.Ceiling(size / 1_048_576.0 / 100.0 / 60.0));   // ~100 MB/s copy

        await Log(_state!.DeviceId, "GraphImport", "Warning",
            $"{name}: PST đang mở trong Outlook — đã yêu cầu người dùng đóng Outlook (~{estMin} phút).");
        ShowPopup(
            "Đang chuyển email sang Microsoft 365.\n\n" +
            $"Vui lòng ĐÓNG Outlook để hệ thống sao lưu dữ liệu (~{estMin} phút cho {gb:F1} GB).\n\n" +
            "Sao lưu xong sẽ có thông báo — lúc đó bạn MỞ LẠI Outlook bình thường, " +
            "quá trình chuyển sẽ tiếp tục chạy nền.",
            "DKS Migration — cần đóng Outlook");

        var deadline = DateTime.UtcNow.AddMinutes(30);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(3000, ct);
            try
            {
                var temp = await CopyPstForReadAsync(pst, ct);
                ShowPopup(
                    "Sao lưu xong! Bạn có thể MỞ LẠI Outlook ngay bây giờ.\n\n" +
                    "Quá trình chuyển email sẽ tiếp tục chạy nền.",
                    "DKS Migration");
                return temp;
            }
            catch (IOException) { /* still locked — keep waiting */ }
        }
        return null;
    }

    /// <summary>Copies a PST to a temp file, opening the source with shared read/write. Returns the
    /// temp path; the caller deletes it when done. Throws IOException if Outlook has it locked.</summary>
    private static async Task<string> CopyPstForReadAsync(string sourcePath, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"dks-import-{Guid.NewGuid():N}.pst");
        try
        {
            await using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, useAsync: true);
            await using var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            await src.CopyToAsync(dst, 1 << 20, ct);
            return temp;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>Shows a non-blocking info popup on the user's desktop (agent runs interactively).</summary>
    private static void ShowPopup(string text, string title)
    {
        try
        {
            // 0x40 MB_ICONINFORMATION | 0x10000 MB_SETFOREGROUND | 0x40000 MB_TOPMOST
            var t = new System.Threading.Thread(() => MessageBoxW(IntPtr.Zero, text, title, 0x40 | 0x10000 | 0x40000))
            { IsBackground = true };
            t.Start();
        }
        catch { /* no interactive desktop — ignore */ }
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

    private string? ResolveM365MailboxUpn()
    {
        var newEmail = NormalizePlainMailbox(_state?.NewEmail);
        var targetMailbox = NormalizePlainMailbox(_state?.TargetMailbox);

        if (!string.IsNullOrWhiteSpace(_state?.TargetMailbox) && targetMailbox == null)
        {
            _logger.LogWarning("TargetMailbox '{TargetMailbox}' is not a plain UPN/email and will not be used for PRF profile creation.", _state.TargetMailbox);
        }

        if (!string.IsNullOrWhiteSpace(_state?.NewEmail) && newEmail == null)
        {
            _logger.LogWarning("NewEmail '{NewEmail}' is not a plain UPN/email and will not be used for PRF profile creation.", _state.NewEmail);
        }

        if (newEmail != null && targetMailbox != null &&
            !newEmail.Equals(targetMailbox, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "TargetMailbox '{TargetMailbox}' differs from NewEmail '{NewEmail}'. Using NewEmail as the M365 sign-in UPN for PRF.",
                targetMailbox, newEmail);
        }

        return newEmail ?? targetMailbox;
    }

    private static string? NormalizePlainMailbox(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        try
        {
            var addr = new MailAddress(trimmed);
            return addr.Address.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
                ? addr.Address
                : null;
        }
        catch
        {
            return null;
        }
    }
}
