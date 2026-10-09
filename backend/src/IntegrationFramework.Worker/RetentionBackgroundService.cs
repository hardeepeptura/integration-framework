using IntegrationFramework.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntegrationFramework.Worker;

/// <summary>
/// Purges old run data in batches so the metadata store stays bounded at
/// millions of runs/month. Only runs when IF_ROLE != "api". Windows are
/// configurable via Retention:* (0 disables that window):
///   Retention:StepRunDays      (default 90)  — step input/output payloads
///   Retention:WebhookEventDays (default 90)  — durable-inbox deliveries
///   Retention:WorkflowRunDays  (default 365) — run headers (cascades steps)
///   Retention:IntervalHours    (default 24)  — purge cadence
/// </summary>
public class RetentionBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<RetentionBackgroundService> logger,
    IConfiguration configuration) : BackgroundService
{
    internal const int DefaultStepRunDays = 90;
    internal const int DefaultWebhookEventDays = 90;
    internal const int DefaultWorkflowRunDays = 365;
    internal const int DefaultIntervalHours = 24;
    private const int BatchSize = 5000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var role = configuration["IF_ROLE"];
        if (role == "api")
        {
            logger.LogInformation("RetentionBackgroundService disabled (IF_ROLE=api).");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, ConfigDays("Retention:IntervalHours", DefaultIntervalHours)));
        logger.LogInformation("RetentionBackgroundService started (every {Hours}h).", interval.TotalHours);

        // Run once at startup, then on the interval.
        while (true)
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
                logger.LogError(ex, "Retention tick failed.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>One purge cycle. Internal for tests. Returns the total rows deleted.</summary>
    internal async Task<int> TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();

        var total = 0;

        var stepDays = ConfigDays("Retention:StepRunDays", DefaultStepRunDays);
        if (stepDays > 0)
        {
            var cutoff = now.AddDays(-stepDays);
            var deleted = await PurgeAsync(db, db.StepRuns.Where(s => s.StartedAt < cutoff), ct);
            logger.LogInformation("Retention: deleted {Count} step runs older than {Cutoff}.", deleted, cutoff);
            total += deleted;
        }

        var eventDays = ConfigDays("Retention:WebhookEventDays", DefaultWebhookEventDays);
        if (eventDays > 0)
        {
            var cutoff = now.AddDays(-eventDays);
            var deleted = await PurgeAsync(db, db.WebhookEvents.Where(e => e.ReceivedAt < cutoff), ct);
            logger.LogInformation("Retention: deleted {Count} webhook events older than {Cutoff}.", deleted, cutoff);
            total += deleted;
        }

        var runDays = ConfigDays("Retention:WorkflowRunDays", DefaultWorkflowRunDays);
        if (runDays > 0)
        {
            var cutoff = now.AddDays(-runDays);
            var deleted = await PurgeAsync(db, db.WorkflowRuns.Where(r => r.StartedAt < cutoff), ct);
            logger.LogInformation("Retention: deleted {Count} workflow runs older than {Cutoff}.", deleted, cutoff);
            total += deleted;
        }

        return total;
    }

    /// <summary>
    /// Deletes matching rows in bounded batches. SQL Server uses ExecuteDelete
    /// (DELETE TOP(n), no data transfer); InMemory (tests) falls back to load+remove.
    /// </summary>
    private async Task<int> PurgeAsync<T>(MetadataDbContext db, IQueryable<T> query, CancellationToken ct)
        where T : class
    {
        var total = 0;
        if (db.Database.IsSqlServer())
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var deleted = await query.Take(BatchSize).ExecuteDeleteAsync(ct);
                if (deleted == 0) break;
                total += deleted;
            }
        }
        else
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var rows = await query.Take(BatchSize).ToListAsync(ct);
                if (rows.Count == 0) break;
                db.RemoveRange(rows);
                await db.SaveChangesAsync(ct);
                total += rows.Count;
            }
        }
        return total;
    }

    /// <summary>
    /// Parsed retention window: a configured value is honored as-is (0 = disabled,
    /// checked by the caller); unparseable/missing falls back to the default.
    /// </summary>
    private int ConfigDays(string key, int defaultDays) =>
        int.TryParse(configuration[key], out var v) ? v : defaultDays;
}
