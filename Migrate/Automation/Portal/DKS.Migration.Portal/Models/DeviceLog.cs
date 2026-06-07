using System.ComponentModel.DataAnnotations;

namespace DKS.Migration.Portal.Models;

public enum LogLevel { Info, Warning, Error, Debug }

public class DeviceLog
{
    [Key]
    public int LogId { get; set; }
    public int DeviceId { get; set; }

    [MaxLength(100)]
    public string? Step { get; set; }

    public LogLevel Level { get; set; } = LogLevel.Info;

    [Required, MaxLength(4000)]
    public string Message { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Device? Device { get; set; }
}
