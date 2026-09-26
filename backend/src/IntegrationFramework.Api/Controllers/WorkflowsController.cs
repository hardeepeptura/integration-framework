using System.Text.Json.Nodes;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Engine;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Api.Controllers;

[ApiController]
[Route("api/workflows")]
public class WorkflowsController(MetadataDbContext db, WorkflowExecutor executor, WorkflowValidator validator) : ControllerBase
{
    public class WorkflowRequest
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public bool? Enabled { get; set; }
        public JsonNode? Graph { get; set; }
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<WorkflowDto>>> List() =>
        Ok((await db.Workflows.OrderBy(w => w.CreatedAt).ToListAsync()).Select(WorkflowDto.From));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WorkflowDto>> Get(Guid id)
    {
        var workflow = await db.Workflows.FindAsync([id]);
        return workflow is null ? NotFound() : Ok(WorkflowDto.From(workflow));
    }

    [HttpPost]
    public async Task<ActionResult<WorkflowDto>> Create([FromBody] WorkflowRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });
        var graphJson = request.Graph?.ToJsonString() ?? "{\"nodes\":[]}";
        var errors = await validator.ValidateAsync(graphJson);
        if (errors.Count > 0)
            return BadRequest(new { error = "Workflow graph failed validation.", errors });

        var workflow = new IntegrationFramework.Core.Entities.Workflow
        {
            Name = request.Name,
            Description = request.Description,
            Enabled = request.Enabled ?? true,
            GraphJson = graphJson
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = workflow.Id }, WorkflowDto.From(workflow));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<WorkflowDto>> Update(Guid id, [FromBody] WorkflowRequest request)
    {
        var workflow = await db.Workflows.FindAsync([id]);
        if (workflow is null) return NotFound();

        if (request.Graph is not null)
        {
            var graphJson = request.Graph.ToJsonString();
            var errors = await validator.ValidateAsync(graphJson);
            if (errors.Count > 0)
                return BadRequest(new { error = "Workflow graph failed validation.", errors });
            workflow.GraphJson = graphJson;
        }
        if (request.Name is not null) workflow.Name = request.Name;
        if (request.Description is not null) workflow.Description = request.Description;
        if (request.Enabled is not null) workflow.Enabled = request.Enabled.Value;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(WorkflowDto.From(workflow));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var workflow = await db.Workflows.FindAsync([id]);
        if (workflow is null) return NotFound();
        db.Workflows.Remove(workflow);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:guid}/validate")]
    public async Task<ActionResult<ValidationResultDto>> Validate(Guid id, [FromBody] JsonNode? body)
    {
        var workflow = await db.Workflows.FindAsync([id]);
        if (workflow is null) return NotFound();
        // Optional body may carry an unsaved graph; fall back to the stored one.
        var graphJson = body?["graph"] is { } graph ? graph.ToJsonString() : workflow.GraphJson;
        var errors = await validator.ValidateAsync(graphJson);
        return Ok(new ValidationResultDto(errors.Count == 0, errors));
    }

    /// <summary>Manual run. Executes inline and returns the completed run (simple + deterministic for Phase 1).</summary>
    [HttpPost("{id:guid}/run")]
    public async Task<ActionResult<RunDto>> Run(Guid id, [FromBody] JsonNode? input)
    {
        var workflow = await db.Workflows.FindAsync([id]);
        if (workflow is null) return NotFound();
        if (!workflow.Enabled)
            return Conflict(new { error = "Workflow is disabled." });

        var run = await ExecuteAsync(workflow, input);
        return Ok(RunDto.From(run));
    }

    private async Task<Core.Entities.WorkflowRun> ExecuteAsync(
        Core.Entities.Workflow workflow, JsonNode? input)
    {
        var env = new Dictionary<string, string>
        {
            ["self_base_url"] = HttpContext.RequestServices
                .GetRequiredService<IConfiguration>()["Self:BaseUrl"] ?? "http://localhost:8000"
        };
        return await executor.ExecuteAsync(workflow, input, env);
    }
}
