namespace IntegrationFramework.Core.Entities;

public class StepRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public WorkflowRun? Run { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty;
    public string Status { get; set; } = "success"; // success | failed | skipped
    public string? InputJson { get; set; }
    public string? OutputJson { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; } = 1;
    public long DurationMs { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
}
