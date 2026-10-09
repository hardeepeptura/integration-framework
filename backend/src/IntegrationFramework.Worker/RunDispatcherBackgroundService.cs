using System.Text.Json.Nodes;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Entities;
using IntegrationFramework.Core.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntegrationFramework.Worker;

/// <summary>
/// Claims queued run items and executes them via the WorkflowExecutor. Only runs
/// when IF_ROLE != "api" (the API pod sets IF_ROLE=api; workers set worker; local
/// single-process dev leaves it unset). Scale workers horizontally: claims use
/// an optimistic-concurrency Version token, so each item is executed exactly once
/// no matter how many replicas race. This is the backpressure layer that keeps
/// the API fast under webhook bursts (the API only enqueues).
/// </summary>
public class RunDispatcherBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<RunDispatcherBackgroundService> logger,
    IConfiguration configuration) : BackgroundService
{
    internal const int DefaultBatchSize = 10;
    internal const double DefaultPollSeconds = 1;
    internal const double DefaultVisibilityMinutes = 5;
    internal const int DefaultRunTimeoutSeconds = 300;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var role = configuration["IF_ROLE"];
        if (role == "api")
        {
            logger.LogInformation("RunDispatcherBackgroundService disabled (IF_ROLE=api).");
            return;
        }

        var poll = TimeSpan.FromSeconds(ConfigDouble("Dispatcher:PollSeconds", DefaultPollSeconds));
        logger.LogInformation(
            "RunDispatcherBackgroundService started (poll {Seconds}s, batch {Batch}).",
            poll.TotalSeconds, ConfigInt("Dispatcher:BatchSize", DefaultBatchSize));

        using var timer = new PeriodicTimer(poll);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dispatcher tick failed.");
            }
        }
    }

    /// <summary>One claim+execute cycle. Internal for tests. Returns the number of items claimed.</summary>
    internal async Task<int> TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var claimed = await ClaimBatchAsync(now, ct);
        if (claimed.Count == 0) return 0;

        // Each item executes in its own scope (own DbContext) — parallel execution
        // multiplies worker throughput by the batch size.
        var tasks = claimed.Select(id => ExecuteItemAsync(id, ct)).ToList();
        await Task.WhenAll(tasks);
        return claimed.Count;
    }

    private async Task<List<Guid>> ClaimBatchAsync(DateTimeOffset now, CancellationToken ct)
    {
        var batch = Math.Clamp(ConfigInt("Dispatcher:BatchSize", DefaultBatchSize), 1, 100);
        var visibility = TimeSpan.FromMinutes(ConfigDouble("Dispatcher:VisibilityMinutes", DefaultVisibilityMinutes));
        var workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();

        // Queued items first, then claim-expired ones (worker crash recovery), oldest first.
        var candidates = await db.RunQueueItems
            .Where(q => q.Status == RunQueueItem.StatusQueued
                        || (q.Status == RunQueueItem.StatusClaimed
                            && q.ClaimedUntil != null && q.ClaimedUntil < now))
            .OrderBy(q => q.EnqueuedAt)
            .Take(batch)
            .ToListAsync(ct);

        var claimed = new List<Guid>();
        foreach (var item in candidates)
        {
            item.Status = RunQueueItem.StatusClaimed;
            item.Attempts += 1;
            item.ClaimedBy = workerId;
            item.ClaimedAt = now;
            item.ClaimedUntil = now + visibility;
            item.Version = Guid.NewGuid(); // concurrency token: only one worker's UPDATE lands
            try
            {
                await db.SaveChangesAsync(ct);
                claimed.Add(item.Id);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another worker claimed it between the query and this save.
                await db.Entry(item).ReloadAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                logger.LogError(ex, "Failed to claim queue item {Id}.", item.Id);
                await db.Entry(item).ReloadAsync(ct);
            }
        }
        return claimed;
    }

    private async Task ExecuteItemAsync(Guid itemId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var item = await db.RunQueueItems.AsNoTracking().FirstAsync(q => q.Id == itemId, ct);
        var timeoutSeconds = Math.Max(10, ConfigInt("Run:TimeoutSeconds", DefaultRunTimeoutSeconds));

        Guid? runId = null;
        string status = "failed";
        string? error = null;

        var workflow = await db.Workflows.FindAsync([item.WorkflowId], ct);
        if (workflow is null)
        {
            error = "The workflow for this queued run no longer exists.";
        }
        else if (!workflow.Enabled)
        {
            error = "The workflow is disabled.";
        }
        else
        {
            JsonNode? input = null;
            if (!string.IsNullOrWhiteSpace(item.InputJson))
            {
                try { input = JsonNode.Parse(item.InputJson); }
                catch { input = null; } // unparseable stored input → run with empty input, mirroring the old webhook path
            }

            var env = new Dictionary<string, string>
            {
                ["self_base_url"] = configuration["Self:BaseUrl"] ?? "http://localhost:8000"
            };

            var executor = scope.ServiceProvider.GetRequiredService<WorkflowExecutor>();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                var run = await executor.ExecuteAsync(workflow, input, env, timeoutCts.Token);
                runId = run.Id;
                status = run.Status; // success | failed
                error = run.Error;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout fired outside the executor's own catch: the run row (already
                // created by the executor) stays as-is; mark the queue item failed.
                error = $"Run timed out after {timeoutSeconds}s.";
            }
        }

        // Finalize the queue item + the linked webhook delivery (same linkage the
        // synchronous webhook path used to apply at the end of a run).
        var trackedItem = await db.RunQueueItems.FirstAsync(q => q.Id == itemId, ct);
        trackedItem.Status = status == "success"
            ? RunQueueItem.StatusCompleted
            : RunQueueItem.StatusFailed;
        trackedItem.RunId = runId;
        trackedItem.CompletedAt = DateTimeOffset.UtcNow;
        trackedItem.Error = error;

        if (item.WebhookEventId is { } eventId)
        {
            var webhookEvent = await db.WebhookEvents.FindAsync([eventId], ct);
            if (webhookEvent is not null)
            {
                webhookEvent.RunId = runId;
                webhookEvent.Status = status == "success" ? "succeeded" : "failed";
                webhookEvent.Error = error;
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Queue item {ItemId} ({TriggerType}) finished: {Status} (run {RunId}).",
            itemId, item.TriggerType, trackedItem.Status, runId);
    }

    private int ConfigInt(string key, int fallback) =>
        int.TryParse(configuration[key], out var v) && v > 0 ? v : fallback;

    private double ConfigDouble(string key, double fallback) =>
        double.TryParse(configuration[key], out var v) && v > 0 ? v : fallback;
}
