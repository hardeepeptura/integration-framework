using System.Text.Json.Nodes;

namespace IntegrationFramework.Core.Engine.Nodes;

/// <summary>
/// Builds a new JSON object by mapping target keys to static values or "$."-references.
/// Config: { "mapping": { "targetKey": "$.steps.x.y" | "literal" | nested-object } }
/// </summary>
public class TransformNode : IWorkflowNode
{
    public string Type => "transform";

    public Task<JsonNode?> ExecuteAsync(NodeExecutionContext context)
    {
        var mapping = context.ConfigNode("mapping") as JsonObject
                      ?? throw new NodeExecutionException(context.NodeId,
                          "transform node requires a 'mapping' object.");

        var result = new JsonObject();
        foreach (var (key, template) in mapping)
            result[key] = context.RunContext.Resolve(template);
        return Task.FromResult<JsonNode?>(result);
    }
}
