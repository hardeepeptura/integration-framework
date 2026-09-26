using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IntegrationFramework.Core.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntegrationFramework.Tests;

/// <summary>Scripted IHttpClientFactory for deterministic outbound HTTP in tests.</summary>
internal class ScriptedHttpClientFactory : IHttpClientFactory
{
    public static ScriptedHttpClientFactory Responder(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new() { Handler = new StubHandler(responder) };

    public DelegatingHandler Handler { private get; init; } = new StubHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

    public HttpClient CreateClient(string name)
    {
        var client = new HttpClient(Handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        return client;
    }

    private class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}

public class ApiIntegrationTests : IClassFixture<ApiIntegrationTests.TestFactory>
{
    public class TestFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("Self:BaseUrl", "http://localhost:8000");
            builder.ConfigureServices(services =>
            {
                // Replace the default IHttpClientFactory with the scripted one so
                // outbound HTTP from workflow nodes never leaves the test process.
                services.RemoveAll<System.Net.Http.IHttpClientFactory>();
                services.AddSingleton<System.Net.Http.IHttpClientFactory>(
                    ScriptedHttpClientFactory.Responder(_ => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"reserved":1,"remaining":99}""",
                            System.Text.Encoding.UTF8, "application/json")
                    }));
            });
        }

        public HttpClient CreateTestClient() => CreateClient();
    }

    private readonly HttpClient _client;

    public ApiIntegrationTests(TestFactory factory)
    {
        _client = factory.CreateTestClient();
    }

    private static JsonObject TransformGraph() => new()
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
                ["id"] = "shape",
                ["type"] = "transform",
                ["config"] = new JsonObject
                {
                    ["mapping"] = new JsonObject
                    {
                        ["sku"] = "$.input.sku",
                        ["qty"] = "$.input.quantity",
                        ["line"] = "{$.input.quantity} x {$.input.sku}"
                    }
                }
            })
    };

    [Fact]
    public async Task Health_returns_ok_with_provider_info()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("ok", body!["status"]!.GetValue<string>());
        Assert.NotNull(body["metadataProvider"]);
    }

    [Fact]
    public async Task Sample_workflow_is_seeded()
    {
        var response = await _client.GetAsync("/api/workflows");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonArray>();
        Assert.NotNull(body);
        Assert.Contains(body!, n => n!["name"]!.GetValue<string>().Contains("Sample"));
    }

    [Fact]
    public async Task Workflow_crud_roundtrip()
    {
        var create = await _client.PostAsJsonAsync("/api/workflows", new
        {
            name = "crud-test",
            description = "d",
            graph = TransformGraph()
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonObject>();
        var id = created!["id"]!.GetValue<string>();

        var get = await _client.GetAsync($"/api/workflows/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var update = await _client.PutAsJsonAsync($"/api/workflows/{id}", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<JsonObject>();
        Assert.False(updated!["enabled"]!.GetValue<bool>());

        var delete = await _client.DeleteAsync($"/api/workflows/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var gone = await _client.GetAsync($"/api/workflows/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Create_with_invalid_graph_returns_400_and_errors()
    {
        var badGraph = new JsonObject
        {
            ["nodes"] = new JsonArray(
                new JsonObject { ["id"] = "n1", ["type"] = "transform", ["config"] = new JsonObject() })
        };
        var response = await _client.PostAsJsonAsync("/api/workflows", new { name = "bad", graph = badGraph });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(body!["errors"]);
    }

    [Fact]
    public async Task Manual_run_executes_transform_flow_and_records_steps()
    {
        var create = await _client.PostAsJsonAsync("/api/workflows", new
        {
            name = "run-test",
            graph = TransformGraph()
        });
        var created = await create.Content.ReadFromJsonAsync<JsonObject>();
        var id = created!["id"]!.GetValue<string>();

        var run = await _client.PostAsJsonAsync($"/api/workflows/{id}/run",
            new { sku = "WIDGET-1", quantity = 4 });
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var runDto = await run.Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal("success", runDto!["status"]!.GetValue<string>());
        Assert.Equal("4 x WIDGET-1", runDto["output"]!["line"]!.GetValue<string>());
        Assert.True((runDto["steps"] as JsonArray)!.Count == 2);

        // Runs list + detail + rerun
        var list = await _client.GetFromJsonAsync<JsonArray>("/api/runs");
        Assert.Contains(list!, r => r!["id"]!.GetValue<string>() == runDto["id"]!.GetValue<string>());

        var detail = await _client.GetFromJsonAsync<JsonObject>($"/api/runs/{runDto["id"]!.GetValue<string>()}");
        Assert.Equal("success", detail!["status"]!.GetValue<string>());

        var rerun = await _client.PostAsync($"/api/runs/{runDto["id"]!.GetValue<string>()}/rerun", null);
        Assert.Equal(HttpStatusCode.OK, rerun.StatusCode);
        var rerunDto = await rerun.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("success", rerunDto!["status"]!.GetValue<string>());
        Assert.NotEqual(runDto["id"]!.GetValue<string>(), rerunDto["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Webhook_trigger_executes_workflow_with_body_input()
    {
        // Reuse the seeded sample workflow: webhook → transform → HTTP POST (stubbed).
        var list = await _client.GetFromJsonAsync<JsonArray>("/api/workflows");
        var sample = list!.First(w => w!["name"]!.GetValue<string>().Contains("Sample"));
        var sampleId = sample!["id"]!.GetValue<string>();

        var response = await _client.PostAsJsonAsync($"/webhook/{sampleId}",
            new { sku = "GIZMO-2", quantity = 5, name = "Hardeep" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("success", body!["status"]!.GetValue<string>());
        // The HTTP node was stubbed to return remaining=99.
        Assert.Equal(99, body["output"]!["body"]!["remaining"]!.GetValue<int>());
    }

    [Fact]
    public async Task Connection_crud_masks_secrets()
    {
        var create = await _client.PostAsJsonAsync("/api/connections", new
        {
            name = "crm-auth",
            kind = "http",
            baseUrl = "http://stub.local",
            authType = "bearer",
            authConfig = new { token_env = "IF_STUB_TOKEN_ENV", token = "literal-secret-do-not-return" }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonObject>();
        var id = created!["id"]!.GetValue<string>();

        var authConfig = created!["authConfig"]!.AsObject();
        Assert.Equal("IF_STUB_TOKEN_ENV", authConfig["token_env"]!.GetValue<string>());
        Assert.Equal("********", authConfig["token"]!.GetValue<string>());

        // Deterministic test-connection via the scripted HTTP factory (200 → success).
        var test = await _client.PostAsync($"/api/connections/{id}/test", null);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var testDto = await test.Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(testDto!["success"]!.GetValue<bool>());

        var delete = await _client.DeleteAsync($"/api/connections/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Db_connection_test_reports_missing_password_env_gracefully()
    {
        var create = await _client.PostAsJsonAsync("/api/connections", new
        {
            name = "meta-db",
            kind = "db",
            dbType = "mssql",
            dbConfig = new
            {
                db_type = "mssql",
                host = "no-such-host.example.com",
                database = "db",
                user = "u",
                password_env = "IF_NOT_SET_DB_PW"
            }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonObject>();
        var id = created!["id"]!.GetValue<string>();

        var test = await _client.PostAsync($"/api/connections/{id}/test", null);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var testDto = await test.Content.ReadFromJsonAsync<JsonObject>();
        Assert.False(testDto!["success"]!.GetValue<bool>()); // host unreachable / env unset → graceful failure
        Assert.False(string.IsNullOrEmpty(testDto["detail"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Demo_systems_support_create_and_reserve()
    {
        var addLead = await _client.PostAsJsonAsync("/demo/crm/leads",
            new { name = "Ada", company = "Eptura", email = "ada@example.com" });
        Assert.Equal(HttpStatusCode.OK, addLead.StatusCode);

        var leads = await _client.GetFromJsonAsync<JsonArray>("/demo/crm/leads");
        Assert.Contains(leads!, l => l!["name"]!.GetValue<string>() == "Ada");

        var reserve = await _client.PostAsJsonAsync("/demo/inventory/reserve",
            new { sku = "WIDGET-1", quantity = 2 });
        Assert.Equal(HttpStatusCode.OK, reserve.StatusCode);
        var reserveDto = await reserve.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(98, reserveDto!["remaining"]!.GetValue<int>());

        var overReserve = await _client.PostAsJsonAsync("/demo/inventory/reserve",
            new { sku = "DOODAD-3", quantity = 999 });
        Assert.Equal(HttpStatusCode.Conflict, overReserve.StatusCode);

        await _client.PostAsync("/demo/crm/reset", null);
        await _client.PostAsync("/demo/inventory/reset", null);
    }

    [Fact]
    public async Task Validate_endpoint_reports_graph_errors()
    {
        var create = await _client.PostAsJsonAsync("/api/workflows", new
        {
            name = "validate-test",
            graph = TransformGraph()
        });
        var created = await create.Content.ReadFromJsonAsync<JsonObject>();
        var id = created!["id"]!.GetValue<string>();

        var ok = await _client.PostAsJsonAsync($"/api/workflows/{id}/validate", new
        {
            graph = TransformGraph()
        });
        var okDto = await ok.Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(okDto!["valid"]!.GetValue<bool>());

        var bad = new JsonObject
        {
            ["nodes"] = new JsonArray(
                new JsonObject { ["id"] = "x", ["type"] = "delay", ["config"] = new JsonObject { ["seconds"] = -1 } })
        };
        var badResult = await _client.PostAsJsonAsync($"/api/workflows/{id}/validate", new { graph = bad });
        var badDto = await badResult.Content.ReadFromJsonAsync<JsonObject>();
        Assert.False(badDto!["valid"]!.GetValue<bool>());
        Assert.NotEmpty(badDto["errors"]!.AsArray());
    }
}
