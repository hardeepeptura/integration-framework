using System.Text.Json.Nodes;

namespace IntegrationFramework.Core.Engine;

/// <summary>Raised when a node fails after exhausting retries.</summary>
public class NodeExecutionException(string nodeId, string message, Exception? inner = null)
    : Exception($"Node '{nodeId}' failed: {message}", inner)
{
    public string NodeId { get; } = nodeId;
}

/// <summary>Context handed to a node runner for one execution.</summary>
public class NodeExecutionContext(
    string nodeId,
    JsonObject config,
    RunContext runContext,
    IServiceProvider services,
    CancellationToken cancellationToken)
{
    public string NodeId { get; } = nodeId;
    public JsonObject Config { get; } = config;
    public RunContext RunContext { get; } = runContext;
    public IServiceProvider Services { get; } = services;
    public CancellationToken CancellationToken { get; } = cancellationToken;

    public string? ConfigString(string name) =>
        Config.TryGetPropertyValue(name, out var v) && v is JsonValue val && val.TryGetValue<string>(out var s)
            ? s
            : null;

    public JsonNode? ConfigNode(string name) =>
        Config.TryGetPropertyValue(name, out var v) ? v : null;

    public int ConfigInt(string name, int fallback) =>
        Config.TryGetPropertyValue(name, out var v) && v is JsonValue val && val.TryGetValue<int>(out var i)
            ? i
            : fallback;
}

/// <summary>A workflow node runner. One implementation per node type.</summary>
public interface IWorkflowNode
{
    string Type { get; }

    Task<JsonNode?> ExecuteAsync(NodeExecutionContext context);
}

/// <summary>Maps node type names to runner instances (DI-backed).</summary>
public class NodeRegistry(IEnumerable<IWorkflowNode> runners)
{
    private readonly Dictionary<string, IWorkflowNode> _runners =
        runners.ToDictionary(r => r.Type, StringComparer.OrdinalIgnoreCase);

    public IWorkflowNode Get(string type) =>
        _runners.TryGetValue(type, out var runner)
            ? runner
            : throw new NodeExecutionException(type, $"Unknown node type '{type}'");

    public bool IsRegistered(string type) => _runners.ContainsKey(type);
}
