using System.Text.Json.Nodes;
using IntegrationFramework.Api.Services;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Engine;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Api.Controllers;

[ApiController]
[Route("api/runs")]
public class RunsController(
    MetadataDbContext db, WorkflowExecutor executor,
    CurrentUserService currentUser, WorkflowAccessService access) : ControllerBase
{
    /// <summary>
    /// Paged, searchable run list. Search matches (case-insensitive): a workflow's
    /// name, the run status (success/failed/running), the error text, or an exact
    /// run id pasted as a guid. Page is 1-based; pageSize defaults to 100 (max 500).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedRunsDto>> List(
        [FromQuery] Guid? workflowId, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var user = await currentUser.ResolveAsync(User);
        var query = db.WorkflowRuns.AsNoTracking().AsQueryable();

        List<Guid>? visible = null;
        if (user is not null && !user.IsAdmin)
        {
            visible = await access.VisibleWorkflowIdsAsync(user);
            query = query.Where(r => visible.Contains(r.WorkflowId));
        }
        if (workflowId is not null) query = query.Where(r => r.WorkflowId == workflowId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var lowered = term.ToLowerInvariant();
            var exactId = Guid.TryParse(term, out var runId) ? runId : (Guid?)null;

            // Workflow-name matches, scoped to what this caller can see.
            var names = await db.Workflows.AsNoTracking()
                .Select(w => new { w.Id, w.Name })
                .ToListAsync();
            var nameMatchedIds = names
                .Where(w => (visible is null || visible.Contains(w.Id))
                            && w.Name.ToLower().Contains(lowered))
                .Select(w => w.Id)
                .ToList();

            query = query.Where(r =>
                r.Status == lowered
                || (r.Error != null && r.Error.ToLower().Contains(lowered))
                || r.Id == exactId
                || nameMatchedIds.Contains(r.WorkflowId));
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(r => r.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new PagedRunsDto(
            items.Select(r => RunDto.From(r, includeSteps: false)).ToList(),
            total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RunDto>> Get(Guid id)
    {
        var run = await db.WorkflowRuns
            .Include(r => r.StepRuns)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);
        if (run is null) return NotFound();

        var user = await currentUser.ResolveAsync(User);
        var workflow = await db.Workflows.FindAsync([run.WorkflowId]);
        if (workflow is not null && !await access.CanViewAsync(user, workflow))
            return NotFound();
        return Ok(RunDto.From(run));
    }

    /// <summary>
    /// Rerun: queues a new run with the original input and returns 202 immediately
    /// (the RunDispatcher executes it, async-first like every other trigger).
    /// </summary>
    [HttpPost("{id:guid}/rerun")]
    public async Task<IActionResult> Rerun(Guid id)
    {
        var previous = await db.WorkflowRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);
        if (previous is null) return NotFound();

        var user = await currentUser.ResolveAsync(User);
        var workflow = await db.Workflows.FindAsync([previous.WorkflowId]);
        if (workflow is null || !await access.CanEditAsync(user, workflow))
            return NotFound(new { error = "The workflow for this run no longer exists." });
        if (!workflow.Enabled)
            return Conflict(new { error = "Workflow is disabled." });

        var item = new Core.Entities.RunQueueItem
        {
            WorkflowId = workflow.Id,
            InputJson = previous.InputJson,
            TriggerType = "manual",
            DedupeKey = $"run:{Guid.NewGuid():N}"
        };
        db.RunQueueItems.Add(item);
        await db.SaveChangesAsync();

        return Accepted(new
        {
            queueId = item.Id,
            workflowId = workflow.Id,
            status = "queued"
        });
    }
}
