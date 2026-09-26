using System.Text.Json.Nodes;

namespace IntegrationFramework.Core.Engine.Nodes;

/// <summary>
/// Iterates an array. Execution of config.body is orchestrated directly by the
/// WorkflowExecutor (so each child node gets a StepRun per iteration); this runner
/// is only a registry/placeholder.
/// Config: { "source": "$.steps.x.items", "body": ["node-a", "node-b"], "maxIterations": 1000 }
/// </summary>
public class LoopNode : IWorkflowNode
{
    public string Type => "loop";

    public Task<JsonNode?> ExecuteAsync(NodeExecutionContext context) =>
        throw new NotSupportedException(
            "Loop nodes are orchestrated by the WorkflowExecutor and cannot run standalone.");
}
