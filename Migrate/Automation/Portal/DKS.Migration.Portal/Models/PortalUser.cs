namespace DKS.Migration.Portal.Models;

/// <summary>
/// A login account for the Portal web UI (distinct from <see cref="MigrationUser"/>,
/// which is an end-user mailbox mapping). Roles: Admin | Operator | Viewer.
/// </summary>
public class PortalUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = PortalRoles.Viewer;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLogin { get; set; }
}

public static class PortalRoles
{
    public const string Admin = "Admin";       // full access + manage portal users
    public const string Operator = "Operator"; // create/edit/delete migration data
    public const string Viewer = "Viewer";     // read-only

    public static readonly string[] All = { Admin, Operator, Viewer };
    public static bool IsValid(string role) => Array.IndexOf(All, role) >= 0;
}
