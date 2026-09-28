using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntegrationFramework.Tests;

public class P0ApiTests : IClassFixture<P0ApiTests.TestFactory>
{
    /// <summary>Same bare-factory + WithWebHostBuilder pattern that starts reliably on .NET 10.</summary>
    public class TestFactory : IDisposable
    {
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _root = new();
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _derived;

        public HttpClient Client { get; }

        public TestFactory()
        {
            _derived = _root.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Self:BaseUrl", "http://localhost:8000");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<System.Net.Http.IHttpClientFactory>();
                    services.AddSingleton<System.Net.Http.IHttpClientFactory>(
                        ScriptedHttpClientFactory.Responder(request =>
                        {
                            var uri = request.RequestUri?.ToString() ?? string.Empty;
                            if (uri.Contains("/oauth/token"))
                            {
                                return new HttpResponseMessage(HttpStatusCode.OK)
                                {
                                    Content = new StringContent(
                                        """{"access_token":"stub-token","expires_in":3600}""",
                                        Encoding.UTF8, "application/json")
                                };
                            }
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
                            };
                        }));
                });
            });
            Client = _derived.CreateClient();
        }

        public void Dispose()
        {
            Client.Dispose();
            _derived.Dispose();
            _root.Dispose();
        }
    }

    private readonly HttpClient _client;

    public P0ApiTests(TestFactory factory) => _client = factory.Client;

    // ----- Entity mappings -----

    private static JsonObject SampleMapping() => new()
    {
        ["fields"] = new JsonArray(
            new JsonObject { ["source"] = "$.input.name", ["target"] = "customer_name", ["required"] = true },
            new JsonObject { ["source"] = "sku", ["target"] = "sku" })
    };

    [Fact]
    public async Task Entity_mapping_crud_and_validate_roundtrip()
    {
        var create = await _client.PostAsJsonAsync("/api/entity-mappings", new
        {
            name = "CRM → Inventory",
            sourceSystem = "demo-crm",
            targetSystem = "demo-inventory",
            mapping = SampleMapping(),
            validationRules = new
            {
                rules = new JsonArray(new JsonObject { ["field"] = "customer_name", ["type"] = "string", ["required"] = true })
            }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonObject>();
        var id = created!["id"]!.GetValue<string>();

        var list = await _client.GetFromJsonAsync<JsonArray>("/api/entity-mappings");
        Assert.Contains(list!, m => m!["id"]!.GetValue<string>() == id);

        // Payload is both run input ("$.input.*") and the relative-path source object.
        var ok = await _client.PostAsJsonAsync($"/api/entity-mappings/{id}/validate",
            new { name = "Ada", sku = "WIDGET-1" });
        var okDto = await ok.Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(okDto!["valid"]!.GetValue<bool>(), okDto["errors"]!.ToJsonString());
        Assert.Equal("WIDGET-1", okDto["mapped"]!["sku"]!.GetValue<string>());

        // Rule violation → invalid with errors.
        var empty = await _client.PostAsJsonAsync($"/api/entity-mappings/{id}/validate", new { });
        var emptyDto = await empty.Content.ReadFromJsonAsync<JsonObject>();
        Assert.False(emptyDto!["valid"]!.GetValue<bool>());
        Assert.NotEmpty(emptyDto["errors"]!.AsArray());

        var delete = await _client.DeleteAsync($"/api/entity-mappings/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var gone = await _client.GetAsync($"/api/entity-mappings/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Workflow_with_entity_mapping_node_executes()
    {
        var createMapping = await _client.PostAsJsonAsync("/api/entity-mappings", new
        {
            name = "lead-mapping",
            mapping = SampleMapping()
        });
        var mapping = await createMapping.Content.ReadFromJsonAsync<JsonObject>();
        var mappingId = mapping!["id"]!.GetValue<string>();

        var graph = new JsonObject
        {
            ["nodes"] = new JsonArray(
                new JsonObject { ["id"] = "trigger", ["type"] = "trigger", ["config"] = new JsonObject { ["trigger"] = "manual" } },
                new JsonObject
                {
                    ["id"] = "map",
                    ["type"] = "entity_mapping",
                    ["config"] = new JsonObject { ["mappingId"] = mappingId }
                })
        };
        var createWf = await _client.PostAsJsonAsync("/api/workflows", new { name = "mapping-flow", graph });
        Assert.Equal(HttpStatusCode.Created, createWf.StatusCode);
        var wf = await createWf.Content.ReadFromJsonAsync<JsonObject>();
        var wfId = wf!["id"]!.GetValue<string>();

        var run = await _client.PostAsJsonAsync($"/api/workflows/{wfId}/run",
            new { name = "Ada", sku = "WIDGET-1" });
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var runDto = await run.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("success", runDto!["status"]!.GetValue<string>());
        Assert.Equal("Ada", runDto["output"]!["customer_name"]!.GetValue<string>());

        // Referencing a nonexistent mapping must fail validation.
        var badGraph = new JsonObject
        {
            ["nodes"] = new JsonArray(
                new JsonObject { ["id"] = "trigger", ["type"] = "trigger", ["config"] = new JsonObject { ["trigger"] = "manual" } },
                new JsonObject
                {
                    ["id"] = "map",
                    ["type"] = "entity_mapping",
                    ["config"] = new JsonObject { ["mappingId"] = "00000000-0000-0000-0000-000000000000" }
                })
        };
        var validate = await _client.PostAsJsonAsync($"/api/workflows/{wfId}/validate", new { graph = badGraph });
        var validateDto = await validate.Content.ReadFromJsonAsync<JsonObject>();
        Assert.False(validateDto!["valid"]!.GetValue<bool>());
        Assert.Contains(validateDto["errors"]!.AsArray(), e => e!.GetValue<string>().Contains("entity mapping"));
    }

    // ----- OAuth2 connection test -----

    [Fact]
    public async Task Oauth2_connection_test_acquires_token()
    {
        var create = await _client.PostAsJsonAsync("/api/connections", new
        {
            name = "crm-oauth",
            kind = "http",
            baseUrl = "http://stub.local",
            authType = "oauth2",
            authConfig = new
            {
                token_url = "https://idp.example.com/oauth/token",
                client_id = "client-1",
                client_secret = "literal-for-test"
            }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonObject>();
        var id = created!["id"]!.GetValue<string>();

        // client_secret is masked in API responses.
        var authConfig = JsonNode.Parse(created!["authConfig"]!.GetValue<string>())!.AsObject();
        Assert.Equal("********", authConfig["client_secret"]!.GetValue<string>());

        var test = await _client.PostAsync($"/api/connections/{id}/test", null);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var testDto = await test.Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(testDto!["success"]!.GetValue<bool>(), testDto["detail"]!.GetValue<string>());
        Assert.Contains("OAuth2 token acquired", testDto["detail"]!.GetValue<string>());

        await _client.DeleteAsync($"/api/connections/{id}");
    }

    // ----- Webhook durability -----

    [Fact]
    public async Task Webhook_delivery_is_persisted_replayable_and_rejections_recorded()
    {
        var workflows = await _client.GetFromJsonAsync<JsonArray>("/api/workflows");
        var sample = workflows!.First(w => w!["name"]!.GetValue<string>().Contains("Sample"));
        var sampleId = sample!["id"]!.GetValue<string>();

        var post = await _client.PostAsJsonAsync($"/webhook/{sampleId}", new { sku = "GIZMO-9", quantity = 2, name = "Durability" });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var postDto = await post.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("success", postDto!["status"]!.GetValue<string>());
        var eventId = postDto["eventId"]!.GetValue<string>();
        var originalRunId = postDto["runId"]!.GetValue<string>();

        // Event is in the durable list with status + run link.
        var events = await _client.GetFromJsonAsync<JsonArray>($"/api/webhook-events?workflowId={sampleId}");
        var evt = events!.First(e => e!["id"]!.GetValue<string>() == eventId)!;
        Assert.Equal("succeeded", evt["status"]!.GetValue<string>());
        Assert.Equal(originalRunId, evt["runId"]!.GetValue<string>());
        Assert.NotNull(evt["body"]);

        var detail = await _client.GetFromJsonAsync<JsonObject>($"/api/webhook-events/{eventId}");
        Assert.Equal("succeeded", detail!["status"]!.GetValue<string>());

        // Replay executes the stored body and creates a NEW run.
        var replay = await _client.PostAsync($"/api/webhook-events/{eventId}/replay", null);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayDto = await replay.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("success", replayDto!["status"]!.GetValue<string>());
        Assert.NotEqual(originalRunId, replayDto["runId"]!.GetValue<string>());

        // Unknown webhook → 404, but the rejection is still recorded for diagnostics.
        var unknown = await _client.PostAsJsonAsync($"/webhook/{Guid.NewGuid()}", new { x = 1 });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var rejected = await _client.GetFromJsonAsync<JsonArray>("/api/webhook-events?status=rejected");
        Assert.NotEmpty(rejected!);

        // Status filter works: our success event does not appear under "rejected".
        Assert.DoesNotContain(rejected!, e => e!["id"]!.GetValue<string>() == eventId);
    }
}
