using System.Text.Json.Nodes;
using IntegrationFramework.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationFramework.Core.Engine.Nodes;

/// <summary>
/// Applies a stored EntityMapping (field mappings + validation rules) to a payload.
/// Config: { "mappingId": "<entity mapping guid>", "source": "$.steps.x.body" (optional;
/// defaults to the run input), "validate": true (default) }
/// Output: the mapped target object. Fails the node when any mapping or validation
/// error occurs (all-or-nothing) so error branches can handle bad payloads.
/// </summary>
public class EntityMappingNode : IWorkflowNode
{
    public string Type => "entity_mapping";

    public async Task<JsonNode?> ExecuteAsync(NodeExecutionContext context)
    {
        var mappingIdRaw = context.ConfigString("mappingId")
                           ?? throw new NodeExecutionException(context.NodeId, "entity_mapping node requires 'mappingId'.");
        if (!Guid.TryParse(mappingIdRaw, out var mappingId))
            throw new NodeExecutionException(context.NodeId, $"mappingId '{mappingIdRaw}' is not a valid GUID.");

        var db = context.Services.GetRequiredService<MetadataDbContext>();
        var mapping = await db.EntityMappings.FindAsync([mappingId], context.CancellationToken)
                      ?? throw new NodeExecutionException(context.NodeId, $"Entity mapping '{mappingIdRaw}' not found.");

        JsonNode? source = null;
        var sourcePath = context.ConfigString("source");
        if (sourcePath is not null)
            source = context.RunContext.ResolvePath(sourcePath);
        else
            source = context.RunContext.Input;

        var validate = context.ConfigNode("validate") is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;

        var errors = EntityMappingApplier.ApplyFieldMappings(mapping.MappingJson, source, context.RunContext, out var mapped);
        if (validate && errors.Count == 0)
            errors.AddRange(EntityMappingApplier.ValidateRules(mapped, mapping.ValidationRulesJson));

        if (errors.Count > 0)
            throw new NodeExecutionException(context.NodeId,
                $"Entity mapping '{mapping.Name}' produced {errors.Count} error(s): {string.Join("; ", errors)}");

        return mapped;
    }
}
