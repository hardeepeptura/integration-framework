using System.Collections.Concurrent;
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
/// Polls for schedule-triggered workflows and runs the due ones. Only runs when
/// IF_ROLE != "api" (the API pod sets IF_ROLE=api; the worker pod sets IF_ROLE=worker;
/// local single-process dev leaves it unset). Single-replica by design — see deploy/README.
/// </summary>
public class SchedulerBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<SchedulerBackgroundService> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _nextRunAt = new();
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var role = configuration["IF_ROLE"];
        if (role == "api")
        {
            logger.LogInformation("SchedulerBackgroundService disabled (IF_ROLE=api).");
            return;
        }

        logger.LogInformation("SchedulerBackgroundService started (poll every {Seconds}s).", PollInterval.TotalSeconds);
        using var timer = new PeriodicTimer(PollInterval);
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
                logger.LogError(ex, "Scheduler tick failed.");
            }
        }
    }

    internal async Task TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();

        var workflows = await db.Workflows.Where(w => w.Enabled).ToListAsync(ct);
        foreach (var workflow in workflows)
        {
            var trigger = SchedulerPolicy.ExtractSchedule(workflow.GraphJson);
            if (trigger is null) continue;

            var next = _nextRunAt.GetOrAdd(workflow.Id, DateTimeOffset.MinValue);
            if (next > now) continue;

            _nextRunAt[workflow.Id] = now.AddSeconds(trigger.IntervalSeconds);

            // Enqueue, don't execute: the RunDispatcher claims it like any other run.
            // The dedupe key (workflow + interval bucket) is enforced by a unique index,
            // so multiple scheduler replicas can never double-fire the same interval.
            // The AnyAsync pre-check covers the InMemory test provider (the index is the
            // hard guarantee on SQL Server; the DbUpdateException catch is the backstop).
            var bucket = now.ToUnixTimeSeconds() / trigger.IntervalSeconds;
            var dedupeKey = $"schedule:{workflow.Id}:{bucket}";
            if (await db.RunQueueItems.AnyAsync(q => q.WorkflowId == workflow.Id && q.DedupeKey == dedupeKey, ct))
                continue;
            db.RunQueueItems.Add(new RunQueueItem
            {
                WorkflowId = workflow.Id,
                InputJson = new JsonObject { ["scheduled"] = true, ["utc"] = now.ToString("O") }.ToJsonString(),
                TriggerType = "schedule",
                DedupeKey = dedupeKey
            });
            logger.LogInformation("Queued scheduled run for workflow '{Name}' ({Id}).", workflow.Name, workflow.Id);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another replica already queued this interval — drop this duplicate.
                foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added))
                    entry.State = EntityState.Detached;
            }
        }
    }
}

/// <summary>Pure policy helpers, unit-testable without a host.</summary>
public static class SchedulerPolicy
{
    public record ScheduleTrigger(int IntervalSeconds);

    public static ScheduleTrigger? ExtractSchedule(string graphJson)
    {
        var root = JsonNode.Parse(graphJson) as JsonObject;
        if (root?["nodes"] is not JsonArray nodes || nodes.Count == 0) return null;
        if (nodes[0] is not JsonObject first || first["type"]?.ToJsonString().Trim('"') != "trigger") return null;
        if (first["config"] is not JsonObject cfg ||
            cfg["trigger"]?.ToJsonString().Trim('"') != "schedule") return null;
        var interval = cfg["intervalSeconds"] is JsonValue v && v.TryGetValue<int>(out var s) && s > 0 ? s : 0;
        return interval > 0 ? new ScheduleTrigger(interval) : null;
    }

    public static bool IsDue(DateTimeOffset? lastRunUtc, int intervalSeconds, DateTimeOffset now) =>
        lastRunUtc is null || (now - lastRunUtc.Value).TotalSeconds >= intervalSeconds;
}
