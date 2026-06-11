using Microsoft.Win32;

namespace DKS.Migration.Agent.Services;

public class ProfileReconfigurer
{
    private readonly ILogger<ProfileReconfigurer> _logger;

    // Outlook 2016+ lưu ở đây
    private static readonly string[] ProfilesPaths =
    {
        @"SOFTWARE\Microsoft\Office\16.0\Outlook\Profiles",
        @"SOFTWARE\Microsoft\Office\15.0\Outlook\Profiles",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows Messaging Subsystem\Profiles"
    };

    public ProfileReconfigurer(ILogger<ProfileReconfigurer> logger) => _logger = logger;

    // Backup toàn bộ profile bằng reg export
    public bool BackupProfile(string profileName, string backupPath)
    {
        try
        {
            Directory.CreateDirectory(backupPath);
            foreach (var regPath in ProfilesPaths)
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{regPath}\{profileName}");
                if (key == null) continue;

                var exportFile = Path.Combine(backupPath,
                    $"OutlookProfile_{profileName}_{DateTime.Now:yyyyMMddHHmmss}.reg");
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "reg.exe",
                    Arguments = $@"export ""HKCU\{regPath}\{profileName}"" ""{exportFile}"" /y",
                    UseShellExecute = false, CreateNoWindow = true
                });
                proc?.WaitForExit(30000);
                _logger.LogInformation("Profile '{Name}' backed up to {File}", profileName, exportFile);
                return true;
            }
            _logger.LogWarning("Profile '{Name}' not found for backup", profileName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup profile failed");
            return false;
        }
    }

    // Tìm profile Exchange/M365 hiện có — không tạo profile giả
    public string? FindExchangeProfile()
    {
        foreach (var regPath in ProfilesPaths)
        {
            using var profilesKey = Registry.CurrentUser.OpenSubKey(regPath);
            if (profilesKey == null) continue;

            foreach (var name in profilesKey.GetSubKeyNames())
            {
                using var pKey = profilesKey.OpenSubKey(name);
                if (pKey == null) continue;

                // Tìm profile có Exchange account (MSEMS service)
                foreach (var clsid in pKey.GetSubKeyNames())
                {
                    using var cKey = pKey.OpenSubKey(clsid);
                    if (cKey == null) continue;
                    foreach (var acct in cKey.GetSubKeyNames())
                    {
                        using var aKey = cKey.OpenSubKey(acct);
                        var svc = aKey?.GetValue("Service Name")?.ToString();
                        if (svc == "MSEMS")
                        {
                            _logger.LogInformation("Found Exchange profile: '{Name}'", name);
                            return name;
                        }
                    }
                }
            }
        }
        return null;
    }

    public bool SetDefaultProfile(string profileName)
    {
        var changed = false;
        foreach (var regPath in ProfilesPaths)
        {
            using var key = Registry.CurrentUser.OpenSubKey(regPath, writable: true);
            if (key == null) continue;
            // Chỉ set nếu profile tồn tại
            if (key.GetSubKeyNames().Contains(profileName, StringComparer.OrdinalIgnoreCase))
            {
                key.SetValue("DefaultProfile", profileName, RegistryValueKind.String);
                _logger.LogInformation("Default profile set to '{Name}' in {Path}", profileName, regPath);
                changed = true;
            }
        }
        return changed;
    }

    // Xóa POP3 account khỏi profile — giữ lại Exchange account
    public bool RemovePop3Account(string profileName, string pop3Email)
    {
        foreach (var regPath in ProfilesPaths)
        {
            using var profileKey = Registry.CurrentUser.OpenSubKey($@"{regPath}\{profileName}");
            if (profileKey == null) continue;

            foreach (var clsid in profileKey.GetSubKeyNames())
            {
                using var cKey = profileKey.OpenSubKey(clsid);
                if (cKey == null) continue;
                foreach (var acct in cKey.GetSubKeyNames())
                {
                    using var aKey = cKey.OpenSubKey(acct);
                    var email = aKey?.GetValue("Email")?.ToString()
                             ?? aKey?.GetValue("Account Name")?.ToString();
                    var pop3Server = aKey?.GetValue("POP3 Server")?.ToString();

                    if (!string.IsNullOrEmpty(pop3Server) &&
                        email?.Equals(pop3Email, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        // Đổi tên thành _DISABLED để disable thay vì xóa (an toàn hơn)
                        using var parentKey = Registry.CurrentUser.OpenSubKey(
                            $@"{regPath}\{profileName}\{clsid}", writable: true);
                        if (parentKey != null)
                        {
                            // Copy sang key mới tên _DISABLED_xxx
                            var disabledName = $"_DISABLED_{acct}";
                            using var dest = parentKey.CreateSubKey(disabledName);
                            if (dest != null)
                            {
                                foreach (var vn in aKey!.GetValueNames())
                                    dest.SetValue(vn, aKey.GetValue(vn)!, aKey.GetValueKind(vn));
                            }
                            parentKey.DeleteSubKeyTree(acct, throwOnMissingSubKey: false);
                            _logger.LogInformation("POP3 account '{Email}' disabled from profile '{Profile}'",
                                pop3Email, profileName);
                            return true;
                        }
                    }
                }
            }
        }
        _logger.LogWarning("POP3 account '{Email}' not found in profile '{Profile}'", pop3Email, profileName);
        return false;
    }

    // Tạo profile mới (hoặc add vào profile có sẵn) bằng PRF file
    // overwrite=true → tạo profile mới clean; overwrite=false → add vào profile hiện có
    public bool AddM365AccountViaPrf(string profileName, string mailbox, bool overwrite = true)
    {
        var outlookPath = FindOutlookExe();
        if (outlookPath == null) { _logger.LogWarning("Outlook.exe not found"); return false; }

        var prfPath = Path.Combine(Path.GetTempPath(), $"dks_add_m365_{Guid.NewGuid():N}.prf");
        try
        {
            // UseAutoDiscover=Yes lets Outlook find the correct M365 endpoint via autodiscover
            // and trigger Modern Auth (OAuth2) on first launch instead of using legacy/Basic auth.
            // ServerName + AuthenticationMethod=16 caused "missing required information" error.
            var prf = "[General]\r\n" +
                      $"ProfileName={profileName}\r\n" +
                      "DefaultProfile=Yes\r\n" +
                      $"OverwriteProfile={(overwrite ? "Yes" : "No")}\r\n\r\n" +
                      "[Service List]\r\n" +
                      "Service1=Microsoft Exchange\r\n\r\n" +
                      "[Microsoft Exchange]\r\n" +
                      "CLSID={ED475410-B0D6-11D2-8C3B-00104B2A6676}\r\n" +
                      "ServiceName=MSEMS\r\n" +
                      "ServiceType=2\r\n" +
                      "DisplayName=Microsoft Exchange\r\n" +
                      "UseAutoDiscover=Yes\r\n" +
                      $"MailboxName={mailbox}\r\n";
            File.WriteAllText(prfPath, prf, System.Text.Encoding.ASCII);
            _logger.LogInformation("PRF generated: {Path}", prfPath);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outlookPath,
                Arguments = $@"/importprf ""{prfPath}""",
                UseShellExecute = true
            });
            _logger.LogInformation("Launched Outlook /importprf for mailbox {Mailbox}", mailbox);

            // Outlook reads the PRF during startup — clean up after a short delay
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
            {
                try { if (File.Exists(prfPath)) File.Delete(prfPath); } catch { }
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddM365AccountViaPrf failed");
            if (File.Exists(prfPath)) File.Delete(prfPath);
            return false;
        }
    }

    // Fallback: mở Outlook thường nếu PRF không dùng được
    public void LaunchOutlookForSignIn()
    {
        var outlookPath = FindOutlookExe();
        if (outlookPath == null) { _logger.LogWarning("Outlook.exe not found"); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = outlookPath,
            UseShellExecute = true
        });
        _logger.LogInformation("Launched Outlook for user sign-in");
    }

    public bool RollbackToProfile(string originalProfileName) => SetDefaultProfile(originalProfileName);

    public string? FindOutlookExe()
    {
        var paths = new[]
        {
            @"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE",
            @"C:\Program Files (x86)\Microsoft Office\root\Office16\OUTLOOK.EXE",
            @"C:\Program Files\Microsoft Office\Office16\OUTLOOK.EXE",
            @"C:\Program Files (x86)\Microsoft Office\Office16\OUTLOOK.EXE"
        };
        foreach (var p in paths) if (File.Exists(p)) return p;

        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\OUTLOOK.EXE");
        return key?.GetValue("")?.ToString();
    }
}
