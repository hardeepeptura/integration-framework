using IntegrationFramework.Api.Services;
using IntegrationFramework.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Api.Controllers;

/// <summary>
/// Dashboard summary for the caller's visible workflows: workflow count, run totals
/// with pass/fail rates, a time-bucketed success/failed series, and the busiest
/// workflows. Range presets run from the last hour up to the last 6 months.
/// Aggregation happens in the DATABASE (GROUP BY / COUNT), never in app memory —
/// the old load-everything-in-the-window approach dies at millions of runs/month.
/// On SQL Server the bucket series is a single grouped query; the InMemory test
/// provider groups a minimal projection client-side (same math, tiny data).
/// </summary>
[ApiController]
[Route("api/dashboard")]
public class DashboardController(MetadataDbContext db, CurrentUserService currentUser) : ControllerBase
{
    private sealed record RangeSpec(TimeSpan Window, long StepSeconds);

    // range → (lookback window, uniform bucket step). Uniform steps keep the grid
    // formula trivial (index = seconds-since-from / step) and match the original
    // bucket semantics (walk back from "now", last bucket partial).
    private static readonly Dictionary<string, RangeSpec> Ranges = new()
    {
        ["hour"] = new(TimeSpan.FromHours(1), 300),           // 12 five-minute buckets
        ["24h"] = new(TimeSpan.FromHours(24), 3600),           // 24 hourly buckets
        ["7d"] = new(TimeSpan.FromDays(7), 86400),            // 7 daily buckets
        ["30d"] = new(TimeSpan.FromDays(30), 86400),          // 30 daily buckets
        ["6m"] = new(TimeSpan.FromDays(183), 30 * 86400L)     // ~6-7 monthly (30-day) buckets
    };

    private sealed class BucketRow
    {
        public int BucketIndex { get; set; }
        public int Total { get; set; }
        public int Success { get; set; }
        public int Failed { get; set; }
    }

    [HttpGet("summary")]
    public async Task<ActionResult<DashboardSummaryDto>> Summary(
        [FromQuery] string range = "6m", [FromQuery] Guid? projectId = null)
    {
        if (!Ranges.TryGetValue(range.ToLowerInvariant(), out var spec))
            return BadRequest(new { error = "Range must be one of: hour, 24h, 7d, 30d, 6m." });

        var user = await currentUser.ResolveAsync(User);
        var now = DateTimeOffset.UtcNow;
        var from = now - spec.Window;

        // Scope to the workflows this caller can see (same rule as the lists),
        // then optionally to one project (Guid.Empty = unassigned workflows only).
        List<Core.Entities.Workflow> workflows;
        if (user is null || user.IsAdmin)
        {
            workflows = await db.Workflows.AsNoTracking().ToListAsync();
        }
        else
        {
            var visible = await VisibleIdsAsync(user);
            workflows = await db.Workflows
                .Where(w => visible.Contains(w.Id))
                .AsNoTracking()
                .ToListAsync();
        }
        if (projectId.HasValue)
            workflows = projectId.Value == Guid.Empty
                ? workflows.Where(w => w.ProjectId == null).ToList()
                : workflows.Where(w => w.ProjectId == projectId.Value).ToList();
        var visibleIds = workflows.Select(w => w.Id).ToHashSet();

        // Totals: COUNT in the database (three cheap indexed counts).
        var baseQuery = db.WorkflowRuns.AsNoTracking()
            .Where(r => r.StartedAt >= from && r.StartedAt <= now && visibleIds.Contains(r.WorkflowId));
        var total = await baseQuery.CountAsync();
        var success = await baseQuery.CountAsync(r => r.Status == "success");
        var failed = await baseQuery.CountAsync(r => r.Status == "failed");
        var running = total - success - failed;
        var completed = success + failed;

        // Busiest workflows: GROUP BY WorkflowId in the database.
        var topRaw = await baseQuery
            .GroupBy(r => r.WorkflowId)
            .Select(g => new
            {
                WorkflowId = g.Key,
                Total = g.Count(),
                Success = g.Count(x => x.Status == "success"),
                Failed = g.Count(x => x.Status == "failed")
            })
            .OrderByDescending(x => x.Total)
            .Take(5)
            .ToListAsync();
        var topWorkflows = topRaw
            .Select(g => new DashboardWorkflowDto(
                g.WorkflowId,
                workflows.FirstOrDefault(w => w.Id == g.WorkflowId)?.Name ?? "(deleted)",
                g.Total, g.Success, g.Failed))
            .ToList();

        // Bucket series. SQL Server: one grouped query with an integer bucket index
        // (seconds since `from` / step). InMemory (tests): same index math client-side
        // over a minimal projection.
        Dictionary<int, BucketRow> grouped;
        if (db.Database.IsSqlServer())
        {
            grouped = await QueryBucketsSqlServerAsync(from, now, spec.StepSeconds, visibleIds);
        }
        else
        {
            var projection = await db.WorkflowRuns.AsNoTracking()
                .Where(r => r.StartedAt >= from && r.StartedAt <= now && visibleIds.Contains(r.WorkflowId))
                .Select(r => new { r.Status, r.StartedAt })
                .ToListAsync();
            grouped = projection
                .GroupBy(r => (int)((r.StartedAt - from).TotalSeconds / spec.StepSeconds))
                .ToDictionary(
                    g => g.Key,
                    g => new BucketRow
                    {
                        BucketIndex = g.Key,
                        Total = g.Count(),
                        Success = g.Count(x => x.Status == "success"),
                        Failed = g.Count(x => x.Status == "failed")
                    });
        }

        // Zero-fill the grid so the chart has contiguous buckets, oldest first.
        var bucketCount = (int)Math.Ceiling(spec.Window.TotalSeconds / spec.StepSeconds);
        var buckets = new List<DashboardBucketDto>(bucketCount);
        for (var i = 0; i < bucketCount; i++)
        {
            grouped.TryGetValue(i, out var row);
            var start = i == 0 ? from : from.AddSeconds(i * spec.StepSeconds);
            var end = i == bucketCount - 1 ? now : from.AddSeconds((i + 1) * spec.StepSeconds);
            buckets.Add(new DashboardBucketDto(
                Start: start,
                End: end,
                Total: row?.Total ?? 0,
                Success: row?.Success ?? 0,
                Failed: row?.Failed ?? 0));
        }

        return Ok(new DashboardSummaryDto(
            Range: range.ToLowerInvariant(),
            From: from,
            To: now,
            WorkflowCount: workflows.Count,
            TotalRuns: total,
            SuccessRuns: success,
            FailedRuns: failed,
            RunningRuns: running,
            SuccessRate: completed == 0 ? 0 : Math.Round(success * 100.0 / completed, 1),
            FailureRate: completed == 0 ? 0 : Math.Round(failed * 100.0 / completed, 1),
            Buckets: buckets,
            TopWorkflows: topWorkflows));
    }

    /// <summary>
    /// One grouped query: bucket index = seconds since `from` / step. Parameters are
    /// positional (@p0, @p1, ...); the workflow IN-list (when scoping a contributor)
    /// appends @p3..@pN. Unit/step are internal constants, never user input.
    /// </summary>
    private async Task<Dictionary<int, BucketRow>> QueryBucketsSqlServerAsync(
        DateTimeOffset from, DateTimeOffset to, long stepSeconds, HashSet<Guid> visibleIds)
    {
        var parameters = new List<object> { from, to, stepSeconds };
        var workflowFilter = "";
        if (visibleIds.Count > 0 && visibleIds.Count < 2000) // otherwise the admin path already skipped scoping
        {
            var names = visibleIds.Select((_, i) => $"@p{3 + i}").ToList();
            parameters.AddRange(visibleIds.Cast<object>());
            workflowFilter = $" AND [WorkflowId] IN ({string.Join(", ", names)})";
        }

        var sql =
            $"""
             SELECT (CAST(DATEDIFF(second, @p0, [StartedAt]) AS bigint) / @p2) AS BucketIndex,
                    COUNT(*) AS Total,
                    SUM(CASE WHEN [Status] = 'success' THEN 1 ELSE 0 END) AS Success,
                    SUM(CASE WHEN [Status] = 'failed' THEN 1 ELSE 0 END) AS Failed
             FROM [WorkflowRuns]
             WHERE [StartedAt] >= @p0 AND [StartedAt] <= @p1{workflowFilter}
             GROUP BY (CAST(DATEDIFF(second, @p0, [StartedAt]) AS bigint) / @p2)
             """;

        var rows = await db.Database
            .SqlQueryRaw<BucketRow>(sql, parameters.ToArray())
            .ToListAsync();
        return rows.ToDictionary(r => r.BucketIndex);
    }

    private async Task<List<Guid>> VisibleIdsAsync(CurrentUser user)
    {
        var sharedIds = await db.WorkflowShares
            .Where(s => s.Email == user.Email)
            .Select(s => s.WorkflowId)
            .ToListAsync();
        return await db.Workflows
            .Where(w => w.OwnerEmail == null || w.OwnerEmail == user.Email || sharedIds.Contains(w.Id))
            .Select(w => w.Id)
            .ToListAsync();
    }
}
