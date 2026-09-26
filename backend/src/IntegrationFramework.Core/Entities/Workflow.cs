namespace IntegrationFramework.Core.Entities;

public class Workflow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string GraphJson { get; set; } = "{\"nodes\":[]}";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<WorkflowRun> Runs { get; set; } = new List<WorkflowRun>();
}
