namespace IntegrationFramework.Core.Entities;

/// <summary>
/// A platform user, provisioned automatically on first login from the corporate
/// SSO principal. The first user of an empty installation becomes an admin.
/// </summary>
public class AppUser
{
    public const string RoleAdmin = "admin";
    public const string RoleContributor = "contributor";

    public static readonly IReadOnlyList<string> ValidRoles = [RoleAdmin, RoleContributor];

    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Lowercase UPN/email from the corporate identity provider (unique).</summary>
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    /// <summary>"admin" or "contributor". Admins see and manage everything.</summary>
    public string Role { get; set; } = RoleContributor;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
