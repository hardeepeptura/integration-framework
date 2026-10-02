namespace IntegrationFramework.Core.Entities;

public class Workflow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string GraphJson { get; set; } = "{\"nodes\":[]}";
    public bool Enabled { get; set; } = true;
    /// <summary>Lowercase email of the owning user. Null = legacy workflow created before
    /// ownership existed: visible to every authenticated user, mutable by admins.</summary>
    public string? OwnerEmail { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<WorkflowRun> Runs { get; set; } = new List<WorkflowRun>();
    public ICollection<WorkflowShare> Shares { get; set; } = new List<WorkflowShare>();
}
