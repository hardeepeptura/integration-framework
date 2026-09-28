using System.Text.Json.Nodes;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Engine;
using IntegrationFramework.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Api.Controllers;

/// <summary>CRUD for entity mappings (field mappings + validation rules) plus a validate endpoint.</summary>
[ApiController]
[Route("api/entity-mappings")]
public class EntityMappingsController(MetadataDbContext db) : ControllerBase
{
    public class EntityMappingRequest
    {
        public string? Name { get; set; }
        public string? SourceSystem { get; set; }
        public string? TargetSystem { get; set; }
        public JsonNode? Mapping { get; set; }
        public JsonNode? ValidationRules { get; set; }
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<EntityMappingDto>>> List() =>
        Ok((await db.EntityMappings.OrderBy(m => m.Name).ToListAsync()).Select(EntityMappingDto.From));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EntityMappingDto>> Get(Guid id)
    {
        var mapping = await db.EntityMappings.FindAsync([id]);
        return mapping is null ? NotFound() : Ok(EntityMappingDto.From(mapping));
    }

    [HttpPost]
    public async Task<ActionResult<EntityMappingDto>> Create([FromBody] EntityMappingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });
        if (request.Mapping is not JsonObject)
            return BadRequest(new { error = "Mapping must be a JSON object with a 'fields' array." });

        var mapping = new EntityMapping
        {
            Name = request.Name,
            SourceSystem = request.SourceSystem,
            TargetSystem = request.TargetSystem,
            MappingJson = request.Mapping.ToJsonString(),
            ValidationRulesJson = request.ValidationRules?.ToJsonString()
        };
        db.EntityMappings.Add(mapping);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = mapping.Id }, EntityMappingDto.From(mapping));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<EntityMappingDto>> Update(Guid id, [FromBody] EntityMappingRequest request)
    {
        var mapping = await db.EntityMappings.FindAsync([id]);
        if (mapping is null) return NotFound();

        if (request.Name is not null) mapping.Name = request.Name;
        if (request.SourceSystem is not null) mapping.SourceSystem = request.SourceSystem;
        if (request.TargetSystem is not null) mapping.TargetSystem = request.TargetSystem;
        if (request.Mapping is not null)
        {
            if (request.Mapping is not JsonObject)
                return BadRequest(new { error = "Mapping must be a JSON object with a 'fields' array." });
            mapping.MappingJson = request.Mapping.ToJsonString();
        }
        if (request.ValidationRules is not null) mapping.ValidationRulesJson = request.ValidationRules.ToJsonString();
        mapping.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(EntityMappingDto.From(mapping));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var mapping = await db.EntityMappings.FindAsync([id]);
        if (mapping is null) return NotFound();
        db.EntityMappings.Remove(mapping);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Applies the mapping (and its validation rules) to a sample payload; returns mapped output and errors.</summary>
    [HttpPost("{id:guid}/validate")]
    public async Task<ActionResult<EntityMappingValidateResultDto>> Validate(Guid id, [FromBody] JsonNode? payload)
    {
        var mapping = await db.EntityMappings.FindAsync([id]);
        if (mapping is null) return NotFound();

        var runContext = new RunContext(payload);
        var errors = EntityMappingApplier.ApplyFieldMappings(mapping.MappingJson, payload, runContext, out var mapped);
        if (errors.Count == 0)
            errors.AddRange(EntityMappingApplier.ValidateRules(mapped, mapping.ValidationRulesJson));

        return Ok(new EntityMappingValidateResultDto(errors.Count == 0, errors, mapped));
    }
}
