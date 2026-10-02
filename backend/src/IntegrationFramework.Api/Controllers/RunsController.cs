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
    [HttpGet]
    public async Task<ActionResult<IEnumerable<RunDto>>> List(
        [FromQuery] Guid? workflowId, [FromQuery] int limit = 50)
    {
        var user = await currentUser.ResolveAsync(User);
        var query = db.WorkflowRuns.AsNoTracking().AsQueryable();
        if (workflowId is not null) query = query.Where(r => r.WorkflowId == workflowId);
        if (user is not null && !user.IsAdmin)
        {
            var visible = await access.VisibleWorkflowIdsAsync(user);
            query = query.Where(r => visible.Contains(r.WorkflowId));
        }
        var runs = await query
            .OrderByDescending(r => r.StartedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Include(r => r.StepRuns)
            .ToListAsync();
        return Ok(runs.Select(r => RunDto.From(r, includeSteps: false)));
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

    [HttpPost("{id:guid}/rerun")]
    public async Task<ActionResult<RunDto>> Rerun(Guid id)
    {
        var previous = await db.WorkflowRuns
            .Include(r => r.StepRuns)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);
        if (previous is null) return NotFound();

        var workflow = await db.Workflows.FindAsync([previous.WorkflowId]);
        if (workflow is null) return NotFound(new { error = "The workflow for this run no longer exists." });
        if (!workflow.Enabled)
            return Conflict(new { error = "Workflow is disabled." });

        var input = string.IsNullOrWhiteSpace(previous.InputJson)
            ? null
            : JsonNode.Parse(previous.InputJson);
        var run = await executor.ExecuteAsync(workflow, input);
        return Ok(RunDto.From(run));
    }
}
