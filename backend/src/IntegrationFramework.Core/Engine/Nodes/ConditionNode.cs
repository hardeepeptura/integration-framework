using System.Text.Json.Nodes;

namespace IntegrationFramework.Core.Engine.Nodes;

/// <summary>
/// Evaluates a comparison and emits { "result": true|false }. The executor routes
/// to config.on_true / config.on_false node-id lists.
/// Config: { "left": "$.steps.x.y", "operator": "eq|ne|gt|lt|contains|exists", "right": <value> }
/// </summary>
public class ConditionNode : IWorkflowNode
{
    public string Type => "condition";

    public Task<JsonNode?> ExecuteAsync(NodeExecutionContext context)
    {
        var left = context.RunContext.Resolve(context.ConfigNode("left"));
        var op = context.ConfigString("operator")
                 ?? throw new NodeExecutionException(context.NodeId, "condition node requires 'operator'.");
        var right = context.RunContext.Resolve(context.ConfigNode("right"));

        var result = op switch
        {
            "exists" => left is not null,
            "eq" => JsonNode.DeepEquals(left, right),
            "ne" => !JsonNode.DeepEquals(left, right),
            "gt" => Compare(left, right) > 0,
            "lt" => Compare(left, right) < 0,
            "contains" => Contains(left, right),
            _ => throw new NodeExecutionException(context.NodeId,
                $"Unknown operator '{op}'. Expected eq, ne, gt, lt, contains or exists.")
        };

        return Task.FromResult<JsonNode?>(new JsonObject { ["result"] = result });
    }

    private static int Compare(JsonNode? left, JsonNode? right)
    {
        if (left is JsonValue l && right is JsonValue r)
        {
            if (l.TryGetValue<decimal>(out var ld) && r.TryGetValue<decimal>(out var rd)) return ld.CompareTo(rd);
            if (l.TryGetValue<DateTimeOffset>(out var ldt) && r.TryGetValue<DateTimeOffset>(out var rdt))
                return ldt.CompareTo(rdt);
        }
        throw new NodeExecutionException("condition", "gt/lt comparisons require numeric or date values.");
    }

    private static bool Contains(JsonNode? left, JsonNode? right)
    {
        if (left is JsonArray arr)
        {
            foreach (var item in arr)
                if (JsonNode.DeepEquals(item, right))
                    return true;
            return false;
        }
        if (left is JsonValue lv && lv.TryGetValue<string>(out var ls) &&
            right is JsonValue rv && rv.TryGetValue<string>(out var rs))
            return ls.Contains(rs, StringComparison.OrdinalIgnoreCase);
        return false;
    }
}
