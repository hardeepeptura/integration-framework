namespace IntegrationFramework.Core.Entities;

/// <summary>
/// A durable run request. All triggers (webhook, manual, scheduled) enqueue here
/// instead of executing inline; worker replicas claim items and execute them via
/// the WorkflowExecutor. The queue IS the backpressure mechanism: the API stays
/// fast under bursts and workers drain at their own pace.
/// </summary>
public class RunQueueItem
{
    public const string StatusQueued = "queued";
    public const string StatusClaimed = "claimed";
    public const string StatusCompleted = "completed";
    public const string StatusFailed = "failed";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowId { get; set; }
    public string? InputJson { get; set; }
    /// <summary>manual | webhook | schedule</summary>
    public string TriggerType { get; set; } = "manual";
    /// <summary>
    /// Always non-null. Unique per workflow: "run:{id}" for one-shot triggers,
    /// "schedule:{workflowId}:{intervalBucket}" for schedules — the unique index
    /// (WorkflowId, DedupeKey) makes double-fired scheduled runs impossible across
    /// replicas (duplicate inserts are rejected).
    /// </summary>
    public string DedupeKey { get; set; } = string.Empty;
    public string Status { get; set; } = StatusQueued;
    public int Attempts { get; set; }
    public DateTimeOffset EnqueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ClaimedBy { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    /// <summary>Visibility timeout: a claimed item whose ClaimedUntil has passed is reclaimable (worker crash recovery).</summary>
    public DateTimeOffset? ClaimedUntil { get; set; }
    /// <summary>The WorkflowRun created when the item executed.</summary>
    public Guid? RunId { get; set; }
    /// <summary>Linked webhook delivery (webhook/replay items) — updated when the run finishes.</summary>
    public Guid? WebhookEventId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    /// <summary>
    /// Optimistic-concurrency claim token: workers race to claim by bumping this;
    /// the UPDATE matches on the original value, so only one worker wins.
    /// Portable across SQL Server and the InMemory test provider.
    /// </summary>
    public Guid Version { get; set; } = Guid.NewGuid();
}
