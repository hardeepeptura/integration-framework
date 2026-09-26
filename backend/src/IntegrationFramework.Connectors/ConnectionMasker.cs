using System.Text.Json.Nodes;

namespace IntegrationFramework.Connectors;

/// <summary>
/// Replaces credential-shaped values (password/token/secret/key fields) with "********"
/// before connection objects are returned by the API. *_env fields (variable NAMES) stay readable.
/// </summary>
public static class ConnectionMasker
{
    private static readonly string[] SecretKeyFragments = ["password", "token", "secret", "apikey", "api_key", "value"];

    public static string? MaskConfigJson(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return configJson;
        if (JsonNode.Parse(configJson) is not JsonObject obj) return configJson;
        MaskInPlace(obj);
        return obj.ToJsonString();
    }

    private static void MaskInPlace(JsonObject obj)
    {
        foreach (var (key, node) in obj)
        {
            if (node is JsonObject child) { MaskInPlace(child); continue; }
            if (node is JsonValue val && val.TryGetValue<string>(out var s))
            {
                var normalized = key.Replace("_", string.Empty).ToLowerInvariant();
                if (normalized.EndsWith("_env") || normalized.EndsWith("env")) continue;
                if (SecretKeyFragments.Any(f => normalized.Contains(f)))
                    obj[key] = "********";
            }
        }
    }
}
