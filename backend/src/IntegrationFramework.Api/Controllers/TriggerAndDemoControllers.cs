using System.Text.Json.Nodes;
using IntegrationFramework.Api.Demo;
using Microsoft.AspNetCore.Authorization;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Engine;
using IntegrationFramework.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Api.Controllers;

/// <summary>
/// Public webhook trigger: POST /webhook/{workflowId} queues a run and returns 202 immediately.
/// Every delivery is persisted BEFORE queueing (durable inbox) and linked to its run by the
/// worker-side RunDispatcher, so failed or interrupted deliveries can be replayed from
/// WebhookEventsController. The 202 contract keeps the API flat under bursts (no inline execution).
/// </summary>
[ApiController]
public class WebhookController(MetadataDbContext db) : ControllerBase
{
    [HttpPost("webhook/{workflowId:guid}")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous] // external systems call this
    public async Task<IActionResult> Post(Guid workflowId, [FromBody] JsonNode? body)
    {
        // Persist the delivery first: never lose an inbound webhook, even if the
        // workflow is missing/disabled or the process dies mid-run.
        var webhookEvent = new WebhookEvent
        {
            WorkflowId = workflowId,
            HeadersJson = RequestHeadersToJson(),
            BodyJson = body?.ToJsonString()
        };
        db.WebhookEvents.Add(webhookEvent);
        await db.SaveChangesAsync();

        var workflow = await db.Workflows.FindAsync([workflowId]);
        if (workflow is null)
            return await RejectAsync(webhookEvent, NotFound(new { error = "No workflow for this webhook URL." }));
        if (!workflow.Enabled)
            return await RejectAsync(webhookEvent, Conflict(new { error = "Workflow is disabled." }));

        var triggerGraph = WorkflowExecutor.ParseGraph(workflow.GraphJson);
        if (triggerGraph.Nodes[0].Type != "trigger" ||
            (triggerGraph.Nodes[0].Config["trigger"]?.ToJsonString().Trim('"') is not ("webhook" or "manual")))
            return await RejectAsync(webhookEvent, Conflict(new { error = "Workflow trigger is not a webhook." }));

        // Queue, don't execute: the RunDispatcher (worker) claims and runs it, then
        // flips this event to succeeded/failed with the run id.
        db.RunQueueItems.Add(new RunQueueItem
        {
            WorkflowId = workflowId,
            InputJson = body?.ToJsonString(),
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

    private async Task<IActionResult> RejectAsync(WebhookEvent webhookEvent, IActionResult result)
    {
        webhookEvent.Status = "rejected";
        webhookEvent.Error = "Workflow missing, disabled, or not webhook-triggered.";
        await db.SaveChangesAsync();
        return result;
    }

    private string RequestHeadersToJson()
    {
        var headers = new JsonObject();
        foreach (var (name, values) in Request.Headers)
            if (!name.StartsWith("X-Forwarded", StringComparison.OrdinalIgnoreCase))
                headers[name] = values.ToString(); // StringValues joins with ", "
        return headers.ToJsonString();
    }
}

/// <summary>Liveness + metadata-provider info.</summary>
[ApiController]
public class HealthController(MetadataDbContext db) : ControllerBase
{
    [HttpGet("health")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous] // liveness probes
    public async Task<IActionResult> Get()
    {
        var provider = db.Database.ProviderName;
        var dbOk = await db.Database.CanConnectAsync();
        return Ok(new { status = dbOk ? "ok" : "degraded", metadataProvider = provider, time = DateTimeOffset.UtcNow });
    }
}

// ----- Demo systems (mock "System A" CRM and "System B" Inventory) -----

[ApiController]
[Microsoft.AspNetCore.Authorization.AllowAnonymous] // mock systems for the sample workflow
[Route("demo/crm")]
public class DemoCrmController(DemoCrmStore store) : ControllerBase
{
    [HttpGet("leads")]
    public IActionResult Leads() => Ok(store.List());

    public class LeadRequest
    {
        public string? Name { get; set; }
        public string? Company { get; set; }
        public string? Email { get; set; }
    }

    [HttpPost("leads")]
    public IActionResult Add([FromBody] LeadRequest request) =>
        string.IsNullOrWhiteSpace(request.Name)
            ? BadRequest(new { error = "Name is required." })
            : Ok(store.Add(request.Name, request.Company ?? string.Empty, request.Email ?? string.Empty));

    [HttpPost("reset")]
    public IActionResult Reset() { store.Reset(); return NoContent(); }
}

// ----- Demo OAuth2 authorization server (token endpoint) + Bearer-secured API -----

/// <summary>
/// Mock OAuth2 token endpoint for demo workflows: client_credentials grant for
/// the fake demo client. Rejects wrong credentials like a real authorization server.
/// </summary>
[ApiController]
[AllowAnonymous] // mock authorization server for the demo workflows
[Route("demo/oauth2")]
public class DemoOAuthController(DemoOAuthStore store) : ControllerBase
{
    [HttpPost("token")]
    public IActionResult Token(
        [FromForm] string? grant_type, [FromForm] string? client_id,
        [FromForm] string? client_secret, [FromForm] string? scope)
    {
        if (grant_type != "client_credentials")
            return BadRequest(new { error = "unsupported_grant_type", error_description = "Only client_credentials is supported by the demo server." });
        try
        {
            return Ok(store.IssueToken(client_id ?? string.Empty, client_secret ?? string.Empty, scope));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "invalid_client", error_description = ex.Message });
        }
    }

    [HttpPost("reset")]
    public IActionResult Reset() { store.Reset(); return NoContent(); }
}

/// <summary>
/// Bearer-secured demo API: 401 without a token issued by the demo authorization
/// server. This is the "protected System A" that OAuth2 demo workflows pull from.
/// </summary>
[ApiController]
[AllowAnonymous] // auth is enforced by the demo bearer check itself
[Route("demo/secure")]
public class DemoSecureApiController(DemoOAuthStore store) : ControllerBase
{
    [HttpGet("orders")]
    public IActionResult Orders()
    {
        var authorization = Request.Headers.Authorization.ToString();
        var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
        if (!store.IsValid(token))
            return Unauthorized(new { error = "missing or invalid bearer token", hint = "POST /demo/oauth2/token (demo-client / demo-secret) first" });
        return Ok(store.Orders());
    }
}

[Microsoft.AspNetCore.Authorization.AllowAnonymous] // mock systems for the sample workflow
[Route("demo/inventory")]
public class DemoInventoryController(DemoInventoryStore store) : ControllerBase
{
    [HttpGet("items")]
    public IActionResult Items() => Ok(store.List());

    public class ReserveRequest
    {
        public string? Sku { get; set; }
        public int Quantity { get; set; }
    }

    [HttpPost("reserve")]
    public IActionResult Reserve([FromBody] ReserveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Sku) || request.Quantity <= 0)
            return BadRequest(new { error = "Sku and positive Quantity are required." });
        try
        {
            return Ok(store.Reserve(request.Sku, request.Quantity));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("reset")]
    public IActionResult Reset() { store.Reset(); return NoContent(); }
}
