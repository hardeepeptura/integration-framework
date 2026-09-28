using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using IntegrationFramework.Core.Entities;

namespace IntegrationFramework.Core.Connectors;

/// <summary>
/// Fetches and caches OAuth2 access tokens for http connections.
/// Supports the client_credentials and refresh_token grants. Secret fields
/// (client_secret, refresh_token) resolve from *_env references at runtime —
/// literal values are allowed for dev/test but never logged.
/// Tokens are cached in-memory per connection and refreshed ahead of expiry.
/// </summary>
public class OAuth2TokenManager(IHttpClientFactory httpClientFactory)
{
    private sealed record CachedToken(string AccessToken, DateTimeOffset RefreshAt);

    // Refresh 60s before actual expiry to tolerate clock skew and in-flight requests.
    internal static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<Guid, CachedToken> _cache = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>Drops any cached token for the connection (e.g. after a 401 mid-run).</summary>
    public void Invalidate(Guid connectionId) => _cache.TryRemove(connectionId, out _);

    public async Task<string> GetAccessTokenAsync(Connection connection, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(connection.Id, out var cached) && cached.RefreshAt > DateTimeOffset.UtcNow)
            return cached.AccessToken;

        var gate = _locks.GetOrAdd(connection.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the lock: another caller may have refreshed already.
            if (_cache.TryGetValue(connection.Id, out cached) && cached.RefreshAt > DateTimeOffset.UtcNow)
                return cached.AccessToken;

            var (accessToken, expiresIn) = await RequestTokenAsync(connection, ct);
            var lifetime = TimeSpan.FromSeconds(Math.Max(1, expiresIn)) - ExpiryMargin;
            if (lifetime <= TimeSpan.Zero) lifetime = TimeSpan.FromSeconds(1);
            _cache[connection.Id] = new CachedToken(accessToken, DateTimeOffset.UtcNow + lifetime);
            return accessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(string AccessToken, int ExpiresIn)> RequestTokenAsync(Connection connection, CancellationToken ct)
    {
        var config = string.IsNullOrWhiteSpace(connection.AuthConfigJson)
            ? new JsonObject()
            : JsonNode.Parse(connection.AuthConfigJson) as JsonObject
              ?? throw new InvalidOperationException("auth_config must be a JSON object.");

        var grantType = String(config, "grant_type") ?? "client_credentials";
        if (grantType is not ("client_credentials" or "refresh_token"))
            throw new InvalidOperationException($"Unsupported OAuth2 grant_type '{grantType}'.");

        var tokenUrl = String(config, "token_url")
                       ?? throw new InvalidOperationException("auth_config is missing 'token_url' for oauth2.");
        var clientId = String(config, "client_id")
                       ?? throw new InvalidOperationException("auth_config is missing 'client_id' for oauth2.");

        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", grantType),
            new("client_id", clientId)
        };

        // client_secret is optional for public clients but included when configured.
        if (TryResolveSecret(config, "client_secret", out var clientSecret))
            fields.Add(new("client_secret", clientSecret));

        if (grantType == "client_credentials")
        {
            var scope = String(config, "scope");
            if (!string.IsNullOrWhiteSpace(scope))
                fields.Add(new("scope", scope));
        }
        else
        {
            var refreshToken = ResolveSecret(config, "refresh_token", "refresh_token_env");
            fields.Add(new("refresh_token", refreshToken));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        using var client = httpClientFactory.CreateClient("oauth2-tokens");
        using var response = await client.SendAsync(request, ct);
        var bodyText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OAuth2 token request to '{tokenUrl}' failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(bodyText, 300)}");

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(bodyText);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidOperationException("OAuth2 token response was not valid JSON.");
        }

        var accessToken = parsed is JsonObject obj &&
                          obj.TryGetPropertyValue("access_token", out var at) &&
                          at is JsonValue atVal && atVal.TryGetValue<string>(out var atS)
            ? atS
            : throw new InvalidOperationException("OAuth2 token response is missing 'access_token'.");
        var expiresIn = parsed["expires_in"] is JsonValue ev && ev.TryGetValue<int>(out var ei) ? ei : 3600;
        return (accessToken, expiresIn);
    }

    private static string? String(JsonObject config, string field) =>
        config.TryGetPropertyValue(field, out var v) && v is JsonValue val && val.TryGetValue<string>(out var s)
            ? s
            : null;

    private static bool TryResolveSecret(JsonObject config, string field, out string value)
    {
        var envName = String(config, $"{field}_env");
        if (!string.IsNullOrWhiteSpace(envName))
        {
            value = PasswordResolver.Resolve(envName);
            return true;
        }
        var literal = String(config, field);
        if (literal is not null)
        {
            value = literal; // allowed for non-secret test/dev use; prefer *_env
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static string ResolveSecret(JsonObject config, string literalField, string envField)
    {
        if (TryResolveSecret(config, literalField, out var value)) return value;
        throw new InvalidOperationException(
            $"auth_config is missing '{envField}' (preferred) or '{literalField}' for oauth2.");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
