using IntegrationFramework.Api.Services;
using IntegrationFramework.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Api.Controllers;

/// <summary>
/// Dashboard summary for the caller's visible workflows: workflow count, run totals
/// with pass/fail rates, a time-bucketed success/failed series, and the busiest
/// workflows. Range presets run from the last hour up to the last 6 months; bucket
/// size adapts (5-min → hourly → daily → monthly).
/// </summary>
[ApiController]
[Route("api/dashboard")]
public class DashboardController(MetadataDbContext db, CurrentUserService currentUser) : ControllerBase
{
    private static readonly Dictionary<string, (TimeSpan Window, TimeSpan Bucket)> Ranges = new()
    {
        // range → (lookback window, bucket size)
        ["hour"] = (TimeSpan.FromHours(1), TimeSpan.FromMinutes(5)),
        ["24h"] = (TimeSpan.FromHours(24), TimeSpan.FromHours(1)),
        ["7d"] = (TimeSpan.FromDays(7), TimeSpan.FromDays(1)),
        ["30d"] = (TimeSpan.FromDays(30), TimeSpan.FromDays(1)),
        ["6m"] = (TimeSpan.FromDays(183), TimeSpan.FromDays(30)) // ~6 monthly buckets
    };

    [HttpGet("summary")]
    public async Task<ActionResult<DashboardSummaryDto>> Summary([FromQuery] string range = "6m")
    {
        if (!Ranges.TryGetValue(range.ToLowerInvariant(), out var spec))
            return BadRequest(new { error = "Range must be one of: hour, 24h, 7d, 30d, 6m." });

        var user = await currentUser.ResolveAsync(User);
        var now = DateTimeOffset.UtcNow;
        var from = now - spec.Window;

        // Scope to the workflows this caller can see.
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
        var workflowIds = workflows.Select(w => w.Id).ToHashSet();

        var runs = await db.WorkflowRuns
            .Where(r => r.StartedAt >= from && r.StartedAt <= now)
            .Select(r => new { r.WorkflowId, r.Status, r.StartedAt })
            .ToListAsync();
        var scopedRuns = runs
            .Where(r => workflowIds.Contains(r.WorkflowId))
            .Select(r => (r.WorkflowId, r.Status, r.StartedAt))
            .ToList();

        var buckets = BuildBuckets(now, spec, scopedRuns);

        var total = scopedRuns.Count;
        var success = scopedRuns.Count(r => r.Status == "success");
        var failed = scopedRuns.Count(r => r.Status == "failed");
        var running = total - success - failed;
        var completed = success + failed;

        var topWorkflows = scopedRuns
            .GroupBy(r => r.WorkflowId)
            .Select(g => (
                WorkflowId: g.Key,
                Name: workflows.FirstOrDefault(w => w.Id == g.Key)?.Name ?? "(deleted)",
                Total: g.Count(),
                Success: g.Count(r => r.Status == "success"),
                Failed: g.Count(r => r.Status == "failed")))
            .OrderByDescending(g => g.Total)
            .Take(5)
            .Select(g => new DashboardWorkflowDto(g.WorkflowId, g.Name, g.Total, g.Success, g.Failed))
            .ToList();

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

    private static List<DashboardBucketDto> BuildBuckets(
        DateTimeOffset now, (TimeSpan Window, TimeSpan Bucket) spec,
        List<(Guid WorkflowId, string Status, DateTimeOffset StartedAt)> runs)
    {
        var result = new List<DashboardBucketDto>();
        var bucketSize = spec.Bucket;

        // Walk backwards from "now", aligning each bucket end on the boundary grid.
        var end = now;
        for (var cursor = now; cursor > now - spec.Window; )
        {
            var bucketStart = cursor - bucketSize;
            if (bucketStart < now - spec.Window) bucketStart = now - spec.Window;

            var inBucket = runs.Where(r => r.StartedAt > bucketStart && r.StartedAt <= end).ToList();
            result.Add(new DashboardBucketDto(
                Start: bucketStart,
                End: end,
                Total: inBucket.Count,
                Success: inBucket.Count(r => r.Status == "success"),
                Failed: inBucket.Count(r => r.Status == "failed")));

            end = bucketStart;
            cursor = bucketStart;
        }
        result.Reverse();
        return result;
    }
}
