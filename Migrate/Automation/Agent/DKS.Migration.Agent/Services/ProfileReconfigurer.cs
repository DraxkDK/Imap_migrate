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

    // Detect user đã sign in xong chưa: profile đã có Exchange (MSEMS) account
    // với mailbox được provision. Sau khi user nhập email + password trên popup,
    // Outlook ghi MSEMS service entry vào profile này → session đã established.
    public bool IsExchangeAccountReady(string profileName)
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
                    var svc = aKey?.GetValue("Service Name")?.ToString();
                    if (svc == "MSEMS")
                    {
                        _logger.LogInformation("Exchange account ready in profile '{Profile}'", profileName);
                        return true;
                    }
                }
            }
        }
        return false;
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

    // Tạo empty profile (không có Exchange service) rồi launch Outlook.
    // Outlook thấy profile trống → tự chạy Add Account wizard → user nhập email M365
    // → autodiscover → Modern Auth. Không dùng PRF vì PRF tạo MSEMS entry thiếu
    // required binary MAPI values → lỗi "missing required information" ngay khi mở.
    public bool CreateEmptyProfileAndLaunch(string profileName)
    {
        var outlookPath = FindOutlookExe();
        if (outlookPath == null) { _logger.LogWarning("Outlook.exe not found"); return false; }

        try
        {
            foreach (var regPath in ProfilesPaths)
            {
                using var profilesKey = Registry.CurrentUser.OpenSubKey(regPath, writable: true);
                if (profilesKey == null) continue;

                // Xóa profile cũ bị broken (nếu có từ lần chạy trước)
                try { profilesKey.DeleteSubKeyTree(profileName, throwOnMissingSubKey: false); } catch { }

                // Tạo profile container trống — không có Exchange service nào
                using (profilesKey.CreateSubKey(profileName)) { }

                // Set làm default
                profilesKey.SetValue("DefaultProfile", profileName, RegistryValueKind.String);
                _logger.LogInformation("Empty profile '{Name}' created and set as default", profileName);

                // Launch Outlook với profile mới — sẽ hiện Add Account wizard
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = outlookPath,
                    Arguments = $@"/profile ""{profileName}""",
                    UseShellExecute = true
                });
                _logger.LogInformation("Launched Outlook with empty profile '{Name}' — user will see Add Account wizard", profileName);
                return true;
            }

            _logger.LogWarning("Could not find Outlook profiles registry path");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateEmptyProfileAndLaunch failed");
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
