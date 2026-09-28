using System.Text.Json.Nodes;
using IntegrationFramework.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Core.Engine;

/// <summary>Validates a workflow graph before save/run. Collects ALL errors rather than failing fast.</summary>
public class WorkflowValidator(MetadataDbContext db)
{
    public static readonly string[] KnownTypes =
        ["trigger", "http_request", "db_query", "transform", "condition", "loop", "delay", "entity_mapping"];

    public async Task<List<string>> ValidateAsync(string graphJson, CancellationToken ct = default)
    {
        var errors = new List<string>();
        WorkflowExecutor.WorkflowGraph graph;
        try
        {
            graph = WorkflowExecutor.ParseGraph(graphJson);
        }
        catch (Exception ex)
        {
            errors.Add($"Invalid graph: {ex.Message}");
            return errors;
        }

        if (graph.Nodes.Count == 0)
        {
            errors.Add("Graph must contain at least one node.");
            return errors;
        }

        var first = graph.Nodes[0];
        if (first.Type != "trigger")
            errors.Add($"First node must be of type 'trigger' (found '{first.Type}' on '{first.Id}').");

        var seen = new HashSet<string>();
        foreach (var node in graph.Nodes)
        {
            if (!seen.Add(node.Id))
                errors.Add($"Duplicate node id '{node.Id}'.");
            ValidateNodeConfig(node, errors, ct);
        }

        // Referenced connection ids must exist.
        var referenced = graph.Nodes
            .Where(n => n.Config.TryGetPropertyValue("connectionId", out var c) && c is not null)
            .Select(n => (Id: n.Id, Raw: n.Config["connectionId"]!.ToJsonString().Trim('"')))
            .ToList();
        foreach (var (nodeId, raw) in referenced)
        {
            if (!Guid.TryParse(raw, out var gid))
            {
                errors.Add($"Node '{nodeId}': connectionId '{raw}' is not a valid GUID.");
                continue;
            }
            if (!await db.Connections.AnyAsync(c => c.Id == gid, ct))
                errors.Add($"Node '{nodeId}': connection '{raw}' does not exist.");
        }

        // Referenced entity mappings must exist.
        var mappingRefs = graph.Nodes
            .Where(n => n.Config.TryGetPropertyValue("mappingId", out var m) && m is not null)
            .Select(n => (Id: n.Id, Raw: n.Config["mappingId"]!.ToJsonString().Trim('"')))
            .ToList();
        foreach (var (nodeId, raw) in mappingRefs)
        {
            if (!Guid.TryParse(raw, out var gid))
            {
                errors.Add($"Node '{nodeId}': mappingId '{raw}' is not a valid GUID.");
                continue;
            }
            if (!await db.EntityMappings.AnyAsync(m => m.Id == gid, ct))
                errors.Add($"Node '{nodeId}': entity mapping '{raw}' does not exist.");
        }

        // Referenced branch/body node ids must exist.
        var allIds = graph.Nodes.Select(n => n.Id).ToHashSet();
        foreach (var node in graph.Nodes)
        {
            foreach (var listField in new[] { "on_true", "on_false", "body" })
            {
                if (node.Config.TryGetPropertyValue(listField, out var listNode) && listNode is JsonArray list)
                    foreach (var refId in list)
                    {
                        var id = refId!.ToJsonString().Trim('"');
                        if (!allIds.Contains(id))
                            errors.Add($"Node '{node.Id}': {listField} references unknown node '{id}'.");
                    }
            }
        }
        return errors;
    }

    private static void ValidateNodeConfig(WorkflowExecutor.GraphNode node, List<string> errors, CancellationToken ct)
    {
        if (!KnownTypes.Contains(node.Type))
        {
            errors.Add($"Node '{node.Id}': unknown node type '{node.Type}'.");
            return;
        }

        string Required(string field)
        {
            errors.Add($"Node '{node.Id}' ({node.Type}): missing required config field '{field}'.");
            return string.Empty;
        }

        var configString = (string field) =>
            node.Config.TryGetPropertyValue(field, out var v) && v is JsonValue val && val.TryGetValue<string>(out var s)
                ? s
                : Required(field);

        switch (node.Type)
        {
            case "trigger":
            {
                var trigger = node.Config.TryGetPropertyValue("trigger", out var t) &&
                              t is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : "manual";
                if (trigger is not ("manual" or "webhook" or "schedule"))
                    errors.Add($"Node '{node.Id}': invalid trigger '{trigger}'.");
                break;
            }
            case "http_request":
            {
                if (!node.Config.TryGetPropertyValue("url", out var u) || u is null)
                    Required("url");
                var method = node.Config.TryGetPropertyValue("method", out var m) &&
                             m is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms.ToUpperInvariant() : "GET";
                if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD"))
                    errors.Add($"Node '{node.Id}': invalid HTTP method '{method}'.");
                break;
            }
            case "db_query":
                _ = configString("connectionId");
                _ = configString("sql");
                break;
            case "transform":
                if (!node.Config.TryGetPropertyValue("mapping", out var mapping) || mapping is not JsonObject)
                    errors.Add($"Node '{node.Id}' (transform): requires a 'mapping' object.");
                break;
            case "condition":
            {
                if (!node.Config.TryGetPropertyValue("left", out var leftNode) || leftNode is null)
                    Required("left");
                var op = node.Config.TryGetPropertyValue("operator", out var o) &&
                         o is JsonValue ov && ov.TryGetValue<string>(out var os) ? os : null;
                if (op is null || op is not ("eq" or "ne" or "gt" or "lt" or "contains" or "exists"))
                    errors.Add($"Node '{node.Id}' (condition): operator must be eq, ne, gt, lt, contains or exists.");
                break;
            }
            case "loop":
            {
                _ = configString("source");
                if (!node.Config.TryGetPropertyValue("body", out var body) || body is not JsonArray arr || arr.Count == 0)
                    errors.Add($"Node '{node.Id}' (loop): requires a non-empty 'body' array.");
                break;
            }
            case "delay":
                if (!node.Config.TryGetPropertyValue("seconds", out var sNode) ||
                    sNode is not JsonValue sVal || !sVal.TryGetValue<int>(out var secs) || secs < 0)
                    errors.Add($"Node '{node.Id}' (delay): requires non-negative integer 'seconds'.");
                break;
            case "entity_mapping":
                _ = configString("mappingId");
                break;
        }
    }
}
