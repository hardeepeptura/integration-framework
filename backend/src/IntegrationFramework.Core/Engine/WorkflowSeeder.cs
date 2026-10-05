using System.Text.Json.Nodes;
using IntegrationFramework.Core.Data;

namespace IntegrationFramework.Core.Engine;

/// <summary>Creates the seeded sample workflows on startup (by name, so upgrades add new samples to existing installs).</summary>
public static class WorkflowSeeder
{
    public const string SampleWorkflowName = "Sample: CRM lead to Inventory reserve";
    public const string ApiToApiWorkflowName = "Sample: API to API - CRM leads to Inventory kits";
    public const string OAuthConnectionName = "Demo OAuth2 API (sample)";
    public const string OAuthApiWorkflowName = "Sample: OAuth2 API pull - secure orders to Inventory";

    public static string BuildSampleGraph() => new JsonObject
    {
        ["nodes"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "trigger",
                ["type"] = "trigger",
                ["config"] = new JsonObject { ["trigger"] = "webhook" }
            },
            new JsonObject
            {
                ["id"] = "transform-lead",
                ["type"] = "transform",
                ["config"] = new JsonObject
                {
                    ["mapping"] = new JsonObject
                    {
                        ["sku"] = "$.input.sku",
                        ["quantity"] = "$.input.quantity",
                        ["summary"] = "Reserve {$.input.quantity} x {$.input.sku} for {$.input.name}"
                    }
                }
            },
            new JsonObject
            {
                ["id"] = "reserve-inventory",
                ["type"] = "http_request",
                ["config"] = new JsonObject
                {
                    ["url"] = "{$.env.self_base_url}/demo/inventory/reserve",
                    ["method"] = "POST",
                    ["body"] = new JsonObject
                    {
                        ["sku"] = "$.steps.transform-lead.sku",
                        ["quantity"] = "$.steps.transform-lead.quantity"
                    },
                ["timeoutSeconds"] = 30
            }
        })
    }.ToJsonString();

    /// <summary>
    /// The OAuth2 connection used by the OAuth2 demo workflow: client_credentials
    /// against the in-app demo authorization server (POST /demo/oauth2/token,
    /// fake credentials demo-client / demo-secret mirroring DemoOAuthStore).
    /// The secret is a fake demo value, masked in API responses; real connections
    /// should use client_secret_env with the secret injected as an env var.
    /// </summary>
    public static IntegrationFramework.Core.Entities.Connection BuildOAuthDemoConnection() => new()
    {
        Name = OAuthConnectionName,
        Kind = "http",
        BaseUrl = "http://localhost:8000/demo/secure",
        AuthType = "oauth2",
        AuthConfigJson = new JsonObject
        {
            ["grant_type"] = "client_credentials",
            ["token_url"] = "http://localhost:8000/demo/oauth2/token",
            ["client_id"] = "demo-client",
            ["client_secret"] = "demo-secret",
            ["scope"] = "orders"
        }.ToJsonString()
    };

    /// <summary>
    /// OAuth2 example: fetch a Bearer token via the connection (engine-driven),
    /// pull orders from the Bearer-secured demo API, map each order, and push a
    /// reservation to the demo Inventory. Shows the connectionId pattern for
    /// authenticated API-to-API flows.
    /// </summary>
    public static string BuildOAuthApiGraph(Guid connectionId) => new JsonObject
    {
        ["nodes"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "trigger",
                ["type"] = "trigger",
                ["config"] = new JsonObject { ["trigger"] = "manual" }
            },
            new JsonObject
            {
                // PULL from the Bearer-secured API; the engine fetches and caches an
                // OAuth2 token via the referenced connection and injects it as Bearer.
                ["id"] = "fetch-orders",
                ["type"] = "http_request",
                ["config"] = new JsonObject
                {
                    ["url"] = "{$.env.self_base_url}/demo/secure/orders",
                    ["method"] = "GET",
                    ["connectionId"] = connectionId.ToString(),
                    ["timeoutSeconds"] = 30
                }
            },
            new JsonObject
            {
                ["id"] = "each-order",
                ["type"] = "loop",
                ["config"] = new JsonObject
                {
                    ["source"] = "$.steps.fetch-orders.body",
                    ["body"] = new JsonArray("map-order", "reserve-order")
                }
            },
            new JsonObject
            {
                ["id"] = "map-order",
                ["type"] = "transform",
                ["config"] = new JsonObject
                {
                    ["mapping"] = new JsonObject
                    {
                        ["sku"] = "$.steps.each-order.value.sku",
                        ["quantity"] = "$.steps.each-order.value.quantity",
                        ["order"] = "{$.steps.each-order.value.orderId}"
                    }
                }
            },
            new JsonObject
            {
                ["id"] = "reserve-order",
                ["type"] = "http_request",
                ["config"] = new JsonObject
                {
                    ["url"] = "{$.env.self_base_url}/demo/inventory/reserve",
                    ["method"] = "POST",
                    ["body"] = new JsonObject
                    {
                        ["sku"] = "$.steps.map-order.sku",
                        ["quantity"] = "$.steps.map-order.quantity"
                    },
                    ["timeoutSeconds"] = 30
                }
            })
    }.ToJsonString();

    /// <summary>
    /// API-to-API example: pull the lead list from System A (demo CRM), loop over the
    /// leads, map each one, and push a kit reservation to System B (demo Inventory).
    /// Pull → transform → push, one call per item: the classic integration pattern.
    /// </summary>
    public static string BuildApiToApiGraph() => new JsonObject
    {
        ["nodes"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "trigger",
                ["type"] = "trigger",
                ["config"] = new JsonObject { ["trigger"] = "manual" }
            },
            new JsonObject
            {
                // PULL from System A: GET the CRM leads list.
                ["id"] = "fetch-leads",
                ["type"] = "http_request",
                ["config"] = new JsonObject
                {
                    ["url"] = "{$.env.self_base_url}/demo/crm/leads",
                    ["method"] = "GET",
                    ["timeoutSeconds"] = 30
                }
            },
            new JsonObject
            {
                // Iterate the pulled list; the current lead is $.steps.each-lead.value.
                ["id"] = "each-lead",
                ["type"] = "loop",
                ["config"] = new JsonObject
                {
                    ["source"] = "$.steps.fetch-leads.body",
                    ["body"] = new JsonArray("map-lead", "reserve-kit")
                }
            },
            new JsonObject
            {
                // Map CRM lead fields → Inventory reserve payload (statics + interpolation).
                ["id"] = "map-lead",
                ["type"] = "transform",
                ["config"] = new JsonObject
                {
                    ["mapping"] = new JsonObject
                    {
                        ["sku"] = "WIDGET-1",
                        ["quantity"] = 1,
                        ["reservedFor"] = "{$.steps.each-lead.value.name}",
                        ["company"] = "{$.steps.each-lead.value.company}"
                    }
                }
            },
            new JsonObject
            {
                // PUSH to System B: POST the mapped payload.
                ["id"] = "reserve-kit",
                ["type"] = "http_request",
                ["config"] = new JsonObject
                {
                    ["url"] = "{$.env.self_base_url}/demo/inventory/reserve",
                    ["method"] = "POST",
                    ["body"] = new JsonObject
                    {
                        ["sku"] = "$.steps.map-lead.sku",
                        ["quantity"] = "$.steps.map-lead.quantity",
                        ["reservedFor"] = "$.steps.map-lead.reservedFor"
                    },
                    ["timeoutSeconds"] = 30
                }
            })
    }.ToJsonString();
}
