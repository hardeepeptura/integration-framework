using System.Text.Json.Nodes;
using IntegrationFramework.Api.Demo;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Engine;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Api.Controllers;

/// <summary>Public webhook trigger: POST /webhook/{workflowId} executes the workflow with the body as input.</summary>
[ApiController]
public class WebhookController(MetadataDbContext db, WorkflowExecutor executor) : ControllerBase
{
    [HttpPost("webhook/{workflowId:guid}")]
    public async Task<IActionResult> Post(Guid workflowId, [FromBody] JsonNode? body)
    {
        var workflow = await db.Workflows.FindAsync([workflowId]);
        if (workflow is null) return NotFound(new { error = "No workflow for this webhook URL." });
        if (!workflow.Enabled)
            return Conflict(new { error = "Workflow is disabled." });

        var triggerGraph = WorkflowExecutor.ParseGraph(workflow.GraphJson);
        if (triggerGraph.Nodes[0].Type != "trigger" ||
            (triggerGraph.Nodes[0].Config["trigger"]?.ToJsonString().Trim('"') is not ("webhook" or "manual")))
            return Conflict(new { error = "Workflow trigger is not a webhook." });

        var env = new Dictionary<string, string>
        {
            ["self_base_url"] = HttpContext.RequestServices
                .GetRequiredService<IConfiguration>()["Self:BaseUrl"] ?? "http://localhost:8000"
        };
        var run = await executor.ExecuteAsync(workflow, body, env);
        return Ok(new { runId = run.Id, status = run.Status, output = RunDto.From(run).Output, error = run.Error });
    }
}

/// <summary>Liveness + metadata-provider info.</summary>
[ApiController]
public class HealthController(MetadataDbContext db) : ControllerBase
{
    [HttpGet("health")]
    public async Task<IActionResult> Get()
    {
        var provider = db.Database.ProviderName;
        var dbOk = await db.Database.CanConnectAsync();
        return Ok(new { status = dbOk ? "ok" : "degraded", metadataProvider = provider, time = DateTimeOffset.UtcNow });
    }
}

// ----- Demo systems (mock "System A" CRM and "System B" Inventory) -----

[ApiController]
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

[ApiController]
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
