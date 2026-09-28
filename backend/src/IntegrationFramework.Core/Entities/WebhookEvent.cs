namespace IntegrationFramework.Core.Entities;

/// <summary>
/// A persisted inbound webhook delivery. Every accepted POST /webhook/{workflowId} is
/// recorded BEFORE execution so deliveries survive crashes and can be replayed.
/// Status: received (not yet executed) | succeeded | failed | rejected (unknown/disabled workflow).
/// </summary>
public class WebhookEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowId { get; set; }
    public string? HeadersJson { get; set; }
    public string? BodyJson { get; set; }
    public string Status { get; set; } = "received";
    public Guid? RunId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
