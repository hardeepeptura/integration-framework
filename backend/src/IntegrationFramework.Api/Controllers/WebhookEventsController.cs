using System.Text.Json.Nodes;
using IntegrationFramework.Api.Services;
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
    MetadataDbContext db, WorkflowExecutor executor, IConfiguration configuration,
    CurrentUserService currentUser, WorkflowAccessService access) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<WebhookEventDto>>> List(
        [FromQuery] Guid? workflowId, [FromQuery] string? status, [FromQuery] int limit = 100)
    {
        var query = db.WebhookEvents.AsNoTracking().OrderByDescending(e => e.ReceivedAt).AsQueryable();
        if (workflowId.HasValue) query = query.Where(e => e.WorkflowId == workflowId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(e => e.Status == status);
        var user = await currentUser.ResolveAsync(User);
        if (user is not null && !user.IsAdmin)
        {
            var visible = await access.VisibleWorkflowIdsAsync(user);
            query = query.Where(e => visible.Contains(e.WorkflowId));
        }
        query = query.Take(Math.Clamp(limit, 1, 500));
        return Ok((await query.ToListAsync()).Select(WebhookEventDto.From));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WebhookEventDto>> Get(Guid id)
    {
        var webhookEvent = await db.WebhookEvents.FindAsync([id]);
        return webhookEvent is null ? NotFound() : Ok(WebhookEventDto.From(webhookEvent));
    }

    /// <summary>
    /// Re-queues the originally delivered body: returns 202 immediately; the
    /// RunDispatcher executes it and flips this event to succeeded/failed.
    /// </summary>
    [HttpPost("{id:guid}/replay")]
    public async Task<IActionResult> Replay(Guid id)
    {
        var webhookEvent = await db.WebhookEvents.FindAsync([id]);
        if (webhookEvent is null) return NotFound();

        var workflow = await db.Workflows.FindAsync([webhookEvent.WorkflowId]);
        if (workflow is null)
            return Conflict(new { error = "The workflow for this event no longer exists." });
        var user = await currentUser.ResolveAsync(User);
        if (!await access.CanEditAsync(user, workflow))
            return NotFound();

        // Back to "received" while the replay is queued; the dispatcher finishes it.
        webhookEvent.Status = "received";
        webhookEvent.RunId = null;
        webhookEvent.Error = null;

        db.RunQueueItems.Add(new Core.Entities.RunQueueItem
        {
            WorkflowId = webhookEvent.WorkflowId,
            InputJson = webhookEvent.BodyJson,
            TriggerType = "webhook",
            DedupeKey = $"run:{Guid.NewGuid():N}",
            WebhookEventId = webhookEvent.Id
        });
        await db.SaveChangesAsync();

        return Accepted(new
        {
            eventId = webhookEvent.Id,
            status = "queued"
        });
    }
}
