using System.Text.Json.Nodes;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Engine;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Api.Controllers;

/// <summary>
/// Durable webhook deliveries: list/filter, inspect, and replay. Replaying re-executes
/// the workflow with the originally delivered body and links the new run to the event.
/// </summary>
[ApiController]
[Route("api/webhook-events")]
public class WebhookEventsController(
    MetadataDbContext db, WorkflowExecutor executor, IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<WebhookEventDto>>> List(
        [FromQuery] Guid? workflowId, [FromQuery] string? status, [FromQuery] int limit = 100)
    {
        var query = db.WebhookEvents.AsNoTracking().OrderByDescending(e => e.ReceivedAt).AsQueryable();
        if (workflowId.HasValue) query = query.Where(e => e.WorkflowId == workflowId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(e => e.Status == status);
        query = query.Take(Math.Clamp(limit, 1, 500));
        return Ok((await query.ToListAsync()).Select(WebhookEventDto.From));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WebhookEventDto>> Get(Guid id)
    {
        var webhookEvent = await db.WebhookEvents.FindAsync([id]);
        return webhookEvent is null ? NotFound() : Ok(WebhookEventDto.From(webhookEvent));
    }

    /// <summary>Re-executes the workflow with the originally delivered body.</summary>
    [HttpPost("{id:guid}/replay")]
    public async Task<ActionResult<object>> Replay(Guid id)
    {
        var webhookEvent = await db.WebhookEvents.FindAsync([id]);
        if (webhookEvent is null) return NotFound();

        var workflow = await db.Workflows.FindAsync([webhookEvent.WorkflowId]);
        if (workflow is null)
            return Conflict(new { error = "The workflow for this event no longer exists." });

        JsonNode? body = null;
        if (!string.IsNullOrWhiteSpace(webhookEvent.BodyJson))
        {
            try { body = JsonNode.Parse(webhookEvent.BodyJson); }
            catch
            {
                return Conflict(new { error = "Stored event body is not valid JSON and cannot be replayed." });
            }
        }

        // Replay with the same env the webhook path provides so $.env.* references resolve.
        var env = new Dictionary<string, string>
        {
            ["self_base_url"] = configuration["Self:BaseUrl"] ?? "http://localhost:8000"
        };
        var run = await executor.ExecuteAsync(workflow, body, env);
        webhookEvent.RunId = run.Id;
        webhookEvent.Status = run.Status == "success" ? "succeeded" : "failed";
        webhookEvent.Error = run.Error;
        await db.SaveChangesAsync();

        return Ok(new
        {
            eventId = webhookEvent.Id,
            runId = run.Id,
            status = run.Status,
            output = RunDto.From(run).Output,
            error = run.Error
        });
    }
}
