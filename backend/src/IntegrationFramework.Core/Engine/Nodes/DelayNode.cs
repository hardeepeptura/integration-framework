using System.Text.Json.Nodes;

namespace IntegrationFramework.Core.Engine.Nodes;

/// <summary>Config: { "seconds": 5 }</summary>
public class DelayNode : IWorkflowNode
{
    public string Type => "delay";

    public async Task<JsonNode?> ExecuteAsync(NodeExecutionContext context)
    {
        var seconds = context.ConfigInt("seconds", -1);
        if (seconds < 0)
            throw new NodeExecutionException(context.NodeId, "delay node requires non-negative 'seconds'.");
        await Task.Delay(TimeSpan.FromSeconds(seconds), context.CancellationToken);
        return new JsonObject { ["delayedSeconds"] = seconds };
    }
}
