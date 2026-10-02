namespace IntegrationFramework.Core.Entities;

/// <summary>
/// Grants a user (by corporate email) access to a workflow they do not own.
/// Owners and admins manage shares; recipients get "view" or "edit" access.
/// </summary>
public class WorkflowShare
{
    public const string PermissionView = "view";
    public const string PermissionEdit = "edit";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowId { get; set; }
    public Workflow? Workflow { get; set; }
    /// <summary>Lowercase corporate email of the grantee (does not have to exist yet).</summary>
    public string Email { get; set; } = string.Empty;
    /// <summary>"view" or "edit". Edit implies run + update; delete/share stay owner+admin.</summary>
    public string Permission { get; set; } = PermissionView;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
