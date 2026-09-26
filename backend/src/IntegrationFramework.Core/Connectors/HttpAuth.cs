using System.Text.Json.Nodes;

namespace IntegrationFramework.Core.Connectors;

/// <summary>
/// Applies a connection's auth profile to an outgoing HttpRequestMessage.
/// Auth config may reference env vars via *_env fields; literal secret values are never stored.
/// </summary>
public static class HttpAuthApplier
{
    public static void Apply(HttpRequestMessage request, string? authType, string? authConfigJson)
    {
        if (authType is null or "none") return;

        var obj = string.IsNullOrWhiteSpace(authConfigJson)
            ? new JsonObject()
            : JsonNode.Parse(authConfigJson) as JsonObject
              ?? throw new InvalidOperationException("auth_config must be a JSON object.");

        string SecretValue(JsonObject cfg, string literalField, string envField)
        {
            if (cfg.TryGetPropertyValue(envField, out var envNode) &&
                envNode is JsonValue envVal && envVal.TryGetValue<string>(out var envName))
                return PasswordResolver.Resolve(envName);
            if (cfg.TryGetPropertyValue(literalField, out var lit) &&
                lit is JsonValue litVal && litVal.TryGetValue<string>(out var litS))
                return litS; // allowed for non-secret test/dev use; prefer *_env
            throw new InvalidOperationException(
                $"auth_config is missing '{envField}' (preferred) or '{literalField}' for auth type '{authType}'.");
        }

        switch (authType)
        {
            case "api_key":
            {
                var headerName = obj.TryGetPropertyValue("header_name", out var hn) &&
                                 hn is JsonValue hv && hv.TryGetValue<string>(out var header)
                    ? header
                    : "X-API-Key";
                request.Headers.Add(headerName, SecretValue(obj, "value", "value_env"));
                break;
            }
            case "bearer":
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SecretValue(obj, "token", "token_env"));
                break;
            case "basic":
            {
                var username = obj.TryGetPropertyValue("username", out var un) &&
                               un is JsonValue uv && uv.TryGetValue<string>(out var user)
                    ? user
                    : throw new InvalidOperationException("auth_config is missing 'username' for basic auth.");
                var password = SecretValue(obj, "password", "password_env");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}")));
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown auth_type '{authType}'.");
        }
    }
}
