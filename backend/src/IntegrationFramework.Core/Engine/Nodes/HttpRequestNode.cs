using System.Text.Json.Nodes;
using IntegrationFramework.Core.Connectors;
using IntegrationFramework.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationFramework.Core.Engine.Nodes;

/// <summary>
/// Executes an HTTP request. Config:
/// { "url": "..." (supports $.-paths), "method": "GET|POST|PUT|PATCH|DELETE",
///   "headers": {...}, "query": {...}, "body": <json template>, "timeoutSeconds": 30,
///   "connectionId": "<optional connection guid for auth>" }
/// Output: { "status": int, "headers": {...}, "body": <parsed json or string> }
/// </summary>
public class HttpRequestNode(IHttpClientFactory httpClientFactory) : IWorkflowNode
{
    public string Type => "http_request";

    public async Task<JsonNode?> ExecuteAsync(NodeExecutionContext context)
    {
        var url = context.RunContext.ResolveString(context.ConfigString("url")
                  ?? throw new NodeExecutionException(context.NodeId, "http_request node requires 'url'."))!
                  .ToJsonString().Trim('"');

        var method = context.ConfigString("method")?.ToUpperInvariant() ?? "GET";
        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD"))
            throw new NodeExecutionException(context.NodeId, $"Invalid HTTP method '{method}'.");

        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        if (context.Config.TryGetPropertyValue("headers", out var headersNode) && headersNode is JsonObject headers)
        {
            var resolvedHeaders = context.RunContext.Resolve(headers);
            foreach (var (name, value) in (resolvedHeaders as JsonObject)!)
                if (value is not null)
                    request.Headers.TryAddWithoutValidation(name, value.ToJsonString().Trim('"'));
        }

        if (context.Config.TryGetPropertyValue("query", out var queryNode) && queryNode is JsonObject query)
        {
            var resolvedQuery = context.RunContext.Resolve(query);
            var pairs = (resolvedQuery as JsonObject)!
                .Where(kv => kv.Value is not null)
                .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!.ToJsonString().Trim('"'))}");
            request.RequestUri = new UriBuilder(url) { Query = string.Join("&", pairs) }.Uri;
        }

        await ApplyAuthAsync(context, request);

        if (context.Config.TryGetPropertyValue("body", out var bodyNode) && bodyNode is not null && method is not ("GET" or "HEAD"))
        {
            var resolvedBody = context.RunContext.Resolve(bodyNode);
            request.Content = new StringContent(
                resolvedBody?.ToJsonString() ?? "null",
                System.Text.Encoding.UTF8,
                "application/json");
        }

        var timeoutSeconds = context.ConfigInt("timeoutSeconds", 30);
        using var client = httpClientFactory.CreateClient("workflow-http");
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        using var response = await client.SendAsync(request, context.CancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(context.CancellationToken);

        JsonObject responseHeaders = new();
        foreach (var (name, values) in response.Headers)
            responseHeaders[name] = string.Join(", ", values);
        foreach (var (name, values) in response.Content.Headers)
            responseHeaders[name] = string.Join(", ", values);

        JsonNode? responseBody;
        try
        {
            responseBody = string.IsNullOrWhiteSpace(responseText) ? null : JsonNode.Parse(responseText);
        }
        catch (System.Text.Json.JsonException)
        {
            responseBody = responseText;
        }

        var result = new JsonObject
        {
            ["status"] = (int)response.StatusCode,
            ["ok"] = response.IsSuccessStatusCode,
            ["headers"] = responseHeaders,
            ["body"] = responseBody
        };

        if (!response.IsSuccessStatusCode)
            throw new NodeExecutionException(context.NodeId,
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} from {url}: {responseText.Truncate(500)}");

        return result;
    }

    private async Task ApplyAuthAsync(NodeExecutionContext context, HttpRequestMessage request)
    {
        var connectionId = context.ConfigString("connectionId");
        if (connectionId is null) return;

        if (!Guid.TryParse(connectionId, out var id))
            throw new NodeExecutionException(context.NodeId, $"connectionId '{connectionId}' is not a valid GUID.");

        var db = context.Services.GetRequiredService<MetadataDbContext>();
        var connection = await db.Connections.FindAsync([id], context.CancellationToken)
                         ?? throw new NodeExecutionException(context.NodeId, $"Connection '{connectionId}' not found.");

        if (connection.Kind != "http")
            throw new NodeExecutionException(context.NodeId, $"Connection '{connection.Name}' is not an http connection.");

        HttpAuthApplier.Apply(request, connection.AuthType, connection.AuthConfigJson);
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
