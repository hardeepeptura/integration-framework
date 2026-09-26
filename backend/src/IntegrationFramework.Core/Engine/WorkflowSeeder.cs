using System.Text.Json.Nodes;
using IntegrationFramework.Core.Data;

namespace IntegrationFramework.Core.Engine;

/// <summary>Creates the seeded sample workflow (webhook → transform → inventory HTTP call) on first startup.</summary>
public static class WorkflowSeeder
{
    public const string SampleWorkflowName = "Sample: CRM lead to Inventory reserve";

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
}
