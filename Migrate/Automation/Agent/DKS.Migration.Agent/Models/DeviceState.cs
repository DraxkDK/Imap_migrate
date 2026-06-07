namespace DKS.Migration.Agent.Models;

public class DeviceState
{
    public int DeviceId { get; set; }
    public int BatchId { get; set; }
    public string? BackupPstPath { get; set; }
    public string? ImportTargetFolder { get; set; }
    public bool CloseOutlookAutomatically { get; set; } = true;
    public bool RollbackEnabled { get; set; } = true;
    public DateTime? CutoverTime { get; set; }
    public string CurrentStep { get; set; } = "Pending";
    public List<OutlookProfile> Profiles { get; set; } = new();
    public List<PstFileInfo> PstFiles { get; set; } = new();

    // From user mapping table
    public string? OldEmail { get; set; }
    public string? NewEmail { get; set; }
    public string? TargetMailbox { get; set; }
    public string? FullName { get; set; }
}

public class OutlookProfile
{
    public string ProfileName { get; set; } = "";
    public List<OutlookAccount> Accounts { get; set; } = new();
}

public class OutlookAccount
{
    public string AccountName { get; set; } = "";
    public string EmailAddress { get; set; } = "";
    public string AccountType { get; set; } = "";
    public string? PstPath { get; set; }
    public bool IsPop3 => AccountType.Equals("POP3", StringComparison.OrdinalIgnoreCase);
}

public class PstFileInfo
{
    public string SourcePath { get; set; } = "";
    public string? BackupPath { get; set; }
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public bool IsExported { get; set; }
    public bool IsImported { get; set; }
}
