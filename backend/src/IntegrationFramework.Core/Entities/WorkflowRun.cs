namespace IntegrationFramework.Core.Entities;

public class WorkflowRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowId { get; set; }
    public Workflow? Workflow { get; set; }
    public string Status { get; set; } = "running"; // running | success | failed
    public string? InputJson { get; set; }
    public string? OutputJson { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public ICollection<StepRun> StepRuns { get; set; } = new List<StepRun>();
}
