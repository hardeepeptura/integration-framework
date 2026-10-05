using System.Text.Json.Nodes;

namespace IntegrationFramework.Api.Demo;

/// <summary>In-memory mock CRM. Acts as "System A" for demo workflows.</summary>
public class DemoCrmStore
{
    private readonly Lock _lock = new();
    private readonly List<JsonObject> _leads = [];

    public DemoCrmStore()
    {
        // One lead so the seeded API-to-API example has data to pull on a fresh install.
        _leads.Add(new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N")[..8],
            ["name"] = "Ada Lovelace",
            ["company"] = "Eptura",
            ["email"] = "ada@example.com",
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    public JsonObject[] List()
    {
        lock (_lock) { return [.. _leads.Select(l => (JsonObject)l.DeepClone())]; }
    }

    public JsonObject Add(string name, string company, string email)
    {
        var lead = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N")[..8],
            ["name"] = name,
            ["company"] = company,
            ["email"] = email,
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O")
        };
        lock (_lock) { _leads.Add(lead); }
        return (JsonObject)lead.DeepClone();
    }

    public void Reset() { lock (_lock) { _leads.Clear(); } }
}

/// <summary>
/// In-memory mock OAuth2 authorization server plus a Bearer-secured "orders API"
/// for demo workflows: issues client_credentials tokens for the fake demo client
/// (demo-client / demo-secret) and validates them for /demo/secure/* requests.
/// The credentials are fake demo values, not real secrets.
/// </summary>
public class DemoOAuthStore
{
    public const string DemoClientId = "demo-client";
    public const string DemoClientSecret = "demo-secret";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _issuedTokens = new();

    /// <summary>The open orders exposed by the Bearer-secured demo API.</summary>
    public JsonObject[] Orders() =>
    [
        new JsonObject { ["orderId"] = "ORD-1001", ["sku"] = "WIDGET-1", ["quantity"] = 2 },
        new JsonObject { ["orderId"] = "ORD-1002", ["sku"] = "GIZMO-2", ["quantity"] = 1 }
    ];

    public JsonObject IssueToken(string clientId, string clientSecret, string? scope)
    {
        lock (_lock)
        {
            if (clientId != DemoClientId || clientSecret != DemoClientSecret)
                throw new InvalidOperationException(
                    "unknown client_id or wrong client_secret (demo credentials: demo-client / demo-secret)");
            var token = $"demo-token-{Guid.NewGuid():N}";
            _issuedTokens[token] = DateTimeOffset.UtcNow.AddHours(1);
            return new JsonObject
            {
                ["access_token"] = token,
                ["token_type"] = "Bearer",
                ["expires_in"] = 3600,
                ["scope"] = string.IsNullOrWhiteSpace(scope) ? "orders" : scope
            };
        }
    }

    public bool IsValid(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        lock (_lock)
            return _issuedTokens.TryGetValue(token, out var expiry) && expiry > DateTimeOffset.UtcNow;
    }

    public void Reset() { lock (_lock) { _issuedTokens.Clear(); } }
}

/// <summary>In-memory mock inventory system. Acts as "System B" for demo workflows.</summary>
public class DemoInventoryStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _stock = new()
    {
        ["WIDGET-1"] = 100,
        ["GIZMO-2"] = 50,
        ["DOODAD-3"] = 10
    };

    public JsonObject[] List()
    {
        lock (_lock)
        {
            return [.. _stock.Select(kv => new JsonObject { ["sku"] = kv.Key, ["stock"] = kv.Value })];
        }
    }

    public JsonObject Reserve(string sku, int quantity)
    {
        lock (_lock)
        {
            if (!_stock.TryGetValue(sku, out var available))
                throw new InvalidOperationException($"Unknown SKU '{sku}'.");
            if (quantity > available)
                throw new InvalidOperationException($"Insufficient stock for '{sku}' (have {available}, need {quantity}).");
            _stock[sku] = available - quantity;
            return new JsonObject { ["sku"] = sku, ["reserved"] = quantity, ["remaining"] = _stock[sku] };
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _stock.Clear();
            _stock["WIDGET-1"] = 100;
            _stock["GIZMO-2"] = 50;
            _stock["DOODAD-3"] = 10;
        }
    }
}
