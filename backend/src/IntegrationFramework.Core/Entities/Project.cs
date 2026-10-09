namespace IntegrationFramework.Core.Entities;

/// <summary>
/// An organizational container that groups workflows (segregation by project).
/// Grouping only: projects do not change who can see/edit a workflow — the
/// existing owner/share/admin access rules stay exactly as they are.
/// </summary>
public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<Workflow> Workflows { get; set; } = new List<Workflow>();
}
