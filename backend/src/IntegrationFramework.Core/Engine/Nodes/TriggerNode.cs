using System.Text.Json.Nodes;

namespace IntegrationFramework.Core.Engine.Nodes;

/// <summary>
/// First node of every workflow. Its output is the run input (webhook body / manual payload).
/// Config: { "trigger": "manual" | "webhook" | "schedule", "intervalSeconds": 60 }
/// </summary>
public class TriggerNode : IWorkflowNode
{
    public string Type => "trigger";

    public Task<JsonNode?> ExecuteAsync(NodeExecutionContext context)
    {
        var trigger = context.ConfigString("trigger") ?? "manual";
        if (trigger is not ("manual" or "webhook" or "schedule"))
            throw new NodeExecutionException(context.NodeId,
                $"Invalid trigger '{trigger}'. Expected manual, webhook or schedule.");
        return Task.FromResult(context.RunContext.Input);
    }
}
