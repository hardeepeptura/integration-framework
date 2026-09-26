using System.Text.Json.Nodes;
using IntegrationFramework.Core.Connectors;
using IntegrationFramework.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationFramework.Core.Engine.Nodes;

/// <summary>
/// Runs SQL against a saved DB connection. Config:
/// { "connectionId": "<guid>", "sql": "SELECT ...", "mode": "query" | "execute",
///   "params": { "name": <value or $.-path> }, "timeoutSeconds": 30 }
/// Output (query): { "rows": [...], "rowCount": n }
/// Output (execute): { "rowsAffected": n }
/// </summary>
public class DbQueryNode(IDbClientFactory dbClientFactory) : IWorkflowNode
{
    public string Type => "db_query";

    public async Task<JsonNode?> ExecuteAsync(NodeExecutionContext context)
    {
        var connectionId = context.ConfigString("connectionId")
                           ?? throw new NodeExecutionException(context.NodeId, "db_query node requires 'connectionId'.");
        var sql = context.ConfigString("sql")
                  ?? throw new NodeExecutionException(context.NodeId, "db_query node requires 'sql'.");
        var mode = context.ConfigString("mode") ?? "query";
        if (mode is not ("query" or "execute"))
            throw new NodeExecutionException(context.NodeId, $"Invalid mode '{mode}'. Expected query or execute.");

        if (!Guid.TryParse(connectionId, out var id))
            throw new NodeExecutionException(context.NodeId, $"connectionId '{connectionId}' is not a valid GUID.");

        var db = context.Services.GetRequiredService<MetadataDbContext>();
        var connection = await db.Connections.FindAsync([id], context.CancellationToken)
                         ?? throw new NodeExecutionException(context.NodeId, $"Connection '{connectionId}' not found.");

        if (connection.Kind != "db" || connection.DbType is null)
            throw new NodeExecutionException(context.NodeId, $"Connection '{connection.Name}' is not a db connection.");

        var config = DbConfigHelper.Parse(connection.DbConfigJson);
        var client = dbClientFactory.Get(config.DbType);

        // Resolve params; bind as positional @p0..@pN and rewrite ":name" tokens in the SQL.
        var parameters = new Dictionary<string, object?>();
        if (context.Config.TryGetPropertyValue("params", out var paramsNode) && paramsNode is JsonObject paramsObj)
        {
            var resolved = context.RunContext.Resolve(paramsObj)!;
            foreach (var (name, value) in (resolved as JsonObject)!)
                parameters[name] = ToScalar(value);
        }

        var (finalSql, positional) = Bind(sql, parameters);
        var result = await client.ExecuteAsync(new DbRequest
        {
            ConnectionString = DbConfigHelper.BuildConnectionString(config),
            Sql = finalSql,
            Parameters = positional,
            CommandTimeoutSeconds = context.ConfigInt("timeoutSeconds", 30),
            IsQuery = mode == "query"
        }, context.CancellationToken);

        return mode == "query"
            ? new JsonObject { ["rows"] = result.Rows.DeepClone(), ["rowCount"] = result.Rows.Count }
            : new JsonObject { ["rowsAffected"] = result.RowsAffected };
    }

    private static object? ToScalar(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<decimal>(out var d) => d,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        _ => node.ToJsonString()
    };

    /// <summary>Rewrites ":name" tokens to positional "@pN" and orders parameters.</summary>
    public static (string Sql, IReadOnlyDictionary<string, object?> Parameters) Bind(
        string sql, IReadOnlyDictionary<string, object?> named)
    {
        var ordered = new Dictionary<string, object?>();
        var index = 0;
        var bound = System.Text.RegularExpressions.Regex.Replace(
            sql,
            @":([A-Za-z_][A-Za-z0-9_]*)",
            m =>
            {
                var name = m.Groups[1].Value;
                if (!named.ContainsKey(name))
                    throw new NodeExecutionException("db_query", $"SQL references parameter ':{name}' which was not supplied.");
                var key = $"p{index++}";
                ordered[key] = named[name];
                return "@" + key;
            });
        return (bound, ordered);
    }
}
