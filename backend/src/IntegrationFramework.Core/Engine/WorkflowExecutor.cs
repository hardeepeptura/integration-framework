using System.Text.Json;
using System.Text.Json.Nodes;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Core.Engine;

/// <summary>Executes workflow graphs, recording a StepRun per node execution.</summary>
public class WorkflowExecutor(
    MetadataDbContext db,
    NodeRegistry registry,
    IServiceProvider services)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public record GraphNode(string Id, string Type, JsonObject Config);
    public record WorkflowGraph(List<GraphNode> Nodes);

    public static WorkflowGraph ParseGraph(string graphJson)
    {
        var root = JsonNode.Parse(graphJson) as JsonObject
                   ?? throw new InvalidOperationException("graph must be a JSON object.");
        var nodesArray = root.TryGetPropertyValue("nodes", out var n) && n is JsonArray arr
            ? arr
            : throw new InvalidOperationException("graph must contain a 'nodes' array.");

        var nodes = new List<GraphNode>();
        foreach (var item in nodesArray)
        {
            if (item is not JsonObject nodeObj)
                throw new InvalidOperationException("each node must be an object.");
            var id = nodeObj["id"]?.ToJsonString().Trim('"')
                     ?? throw new InvalidOperationException("node is missing 'id'.");
            var type = nodeObj["type"]?.ToJsonString().Trim('"')
                       ?? throw new InvalidOperationException($"node '{id}' is missing 'type'.");
            var config = nodeObj.TryGetPropertyValue("config", out var cfg) && cfg is JsonObject c
                ? (JsonObject)c.DeepClone()
                : new JsonObject();
            nodes.Add(new GraphNode(id, type, config));
        }
        return new WorkflowGraph(nodes);
    }

    public async Task<WorkflowRun> ExecuteAsync(
        Workflow workflow, JsonNode? input, IReadOnlyDictionary<string, string>? env = null,
        CancellationToken cancellationToken = default)
    {
        var graph = ParseGraph(workflow.GraphJson);

        var run = new WorkflowRun
        {
            WorkflowId = workflow.Id,
            InputJson = input?.ToJsonString(),
            StartedAt = DateTimeOffset.UtcNow
        };
        db.WorkflowRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        var runContext = new RunContext(input, env);

        try
        {
            // Nodes referenced by condition branches (on_true/on_false) or loop bodies are
            // executed BY those constructs, not as part of the main sequence.
            var nested = GetNestedNodeIds(graph);
            var mainIds = graph.Nodes.Select(n => n.Id).Where(id => !nested.Contains(id)).ToList();
            await ExecuteSequenceAsync(mainIds, graph, runContext, run, cancellationToken);
            run.Status = "success";
            run.OutputJson = mainIds.Count > 0
                ? runContext.GetStepOutput(mainIds[^1])?.ToJsonString()
                : null;
        }
        catch (Exception ex) when (ex is NodeExecutionException or OperationCanceledException
                                       or InvalidOperationException or NotSupportedException)
        {
            run.Status = "failed";
            run.Error = ex.Message;
        }
        finally
        {
            run.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        return run;
    }

    internal static HashSet<string> GetNestedNodeIds(WorkflowGraph graph)
    {
        var nested = new HashSet<string>();
        foreach (var node in graph.Nodes)
        {
            foreach (var field in new[] { "on_true", "on_false", "body" })
            {
                if (node.Config.TryGetPropertyValue(field, out var listNode) && listNode is JsonArray list)
                    foreach (var refId in list)
                        nested.Add(refId!.ToJsonString().Trim('"'));
            }
        }
        return nested;
    }

    private async Task ExecuteSequenceAsync(
        IReadOnlyList<string> nodeIds, WorkflowGraph graph, RunContext runContext,
        WorkflowRun run, CancellationToken ct)
    {
        var nodesById = graph.Nodes.ToDictionary(n => n.Id);
        foreach (var nodeId in nodeIds)
        {
            ct.ThrowIfCancellationRequested();
            if (!nodesById.TryGetValue(nodeId, out var node))
                throw new NodeExecutionException(nodeId, $"Referenced node '{nodeId}' does not exist.");
            await ExecuteNodeAsync(node, graph, runContext, run, ct);
        }
    }

    private async Task ExecuteNodeAsync(
        GraphNode node, WorkflowGraph graph, RunContext runContext, WorkflowRun run, CancellationToken ct)
    {
        var attempts = node.Config.TryGetPropertyValue("retry", out var retryNode) &&
                       retryNode is JsonObject retryCfg &&
                       retryCfg.TryGetPropertyValue("attempts", out var attNode) &&
                       attNode is JsonValue attVal && attVal.TryGetValue<int>(out var att)
            ? Math.Max(1, att)
            : 1;
        var backoffSeconds = node.Config.TryGetPropertyValue("retry", out var retryNode2) &&
                             retryNode2 is JsonObject retryCfg2 &&
                             retryCfg2.TryGetPropertyValue("backoffSeconds", out var backNode) &&
                             backNode is JsonValue backVal && backVal.TryGetValue<int>(out var back)
            ? Math.Max(0, back)
            : 0;
        var onError = node.Config.TryGetPropertyValue("onError", out var errNode) &&
                      errNode is JsonValue errVal && errVal.TryGetValue<string>(out var err)
            ? err
            : "stop";

        var stepRun = new StepRun
        {
            RunId = run.Id,
            NodeId = node.Id,
            NodeType = node.Type,
            StartedAt = DateTimeOffset.UtcNow
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            JsonNode? output = node.Type switch
            {
                "loop" => await ExecuteLoopAsync(node, graph, runContext, run, ct),
                _ => await ExecuteWithRetriesAsync(node, runContext, attempts, backoffSeconds, ct)
            };

            sw.Stop();
            stepRun.OutputJson = output?.ToJsonString();
            stepRun.DurationMs = sw.ElapsedMilliseconds;
            stepRun.Attempts = attempts;
            stepRun.Status = "success";
            db.StepRuns.Add(stepRun);
            await db.SaveChangesAsync(ct);

            // Condition nodes route execution to on_true / on_false sequences.
            if (node.Type == "condition")
            {
                var conditionResult = output?["result"]?.GetValue<bool>()
                                      ?? throw new NodeExecutionException(node.Id, "condition node did not produce a boolean result.");
                var branch = conditionResult ? "on_true" : "on_false";
                if (node.Config.TryGetPropertyValue(branch, out var branchNode) && branchNode is JsonArray branchIds)
                    await ExecuteSequenceAsync(
                        branchIds.Select(b => b!.ToJsonString().Trim('"')).ToList(), graph, runContext, run, ct);
            }
        }
        catch (Exception ex) when (ex is NodeExecutionException or NotSupportedException
                                       or InvalidOperationException or System.Net.Http.HttpRequestException
                                       or TaskCanceledException or DbUpdateException)
        {
            sw.Stop();
            stepRun.DurationMs = sw.ElapsedMilliseconds;
            stepRun.Attempts = attempts;
            stepRun.Status = "failed";
            stepRun.Error = ex.Message;
            db.StepRuns.Add(stepRun);
            await db.SaveChangesAsync(ct);

            if (onError != "continue")
                throw new NodeExecutionException(node.Id, ex.Message, ex);
        }
    }

    private async Task<JsonNode?> ExecuteWithRetriesAsync(
        GraphNode node, RunContext runContext, int attempts, int backoffSeconds, CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var runner = registry.Get(node.Type);
                var output = await runner.ExecuteAsync(new NodeExecutionContext(
                    node.Id, node.Config, runContext, services, ct));
                runContext.SetStepOutput(node.Id, output);
                return output;
            }
            catch (Exception ex) when (attempt < attempts && ex is not OperationCanceledException)
            {
                lastError = ex;
                if (backoffSeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct);
            }
        }
        throw lastError ?? new NodeExecutionException(node.Id, "node failed without exception details.");
    }

    private async Task<JsonNode?> ExecuteLoopAsync(
        GraphNode loopNode, WorkflowGraph graph, RunContext runContext, WorkflowRun run, CancellationToken ct)
    {
        var sourcePath = loopNode.Config.TryGetPropertyValue("source", out var srcNode) &&
                         srcNode is JsonValue srcVal && srcVal.TryGetValue<string>(out var src)
            ? src
            : throw new NodeExecutionException(loopNode.Id, "loop node requires 'source' path.");
        var source = runContext.ResolvePath(sourcePath)
                     as JsonArray
                     ?? throw new NodeExecutionException(loopNode.Id, $"loop source '{sourcePath}' did not resolve to an array.");

        var maxIterations = loopNode.Config.TryGetPropertyValue("maxIterations", out var maxNode) &&
                            maxNode is JsonValue maxVal && maxVal.TryGetValue<int>(out var max)
            ? max
            : 1000;
        if (source.Count > maxIterations)
            throw new NodeExecutionException(loopNode.Id,
                $"loop source has {source.Count} items, exceeding maxIterations {maxIterations}.");

        var bodyIds = loopNode.Config.TryGetPropertyValue("body", out var bodyNode) && bodyNode is JsonArray body
            ? body.Select(b => b!.ToJsonString().Trim('"')).ToList()
            : [];

        JsonNode? lastItem = null;
        var index = 0;
        foreach (var item in source)
        {
            ct.ThrowIfCancellationRequested();
            lastItem = item?.DeepClone();
            // Expose the current item as the loop node's output while its body executes.
            runContext.SetStepOutput(loopNode.Id, new JsonObject
            {
                ["index"] = index,
                ["value"] = item?.DeepClone()
            });
            await ExecuteSequenceAsync(bodyIds, graph, runContext, run, ct);
            index++;
        }

        var loopOutput = new JsonObject
        {
            ["count"] = source.Count,
            ["value"] = lastItem?.DeepClone()
        };
        runContext.SetStepOutput(loopNode.Id, loopOutput);
        return loopOutput;
    }
}
