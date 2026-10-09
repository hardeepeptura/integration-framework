using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IntegrationFramework.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntegrationFramework.Tests;

/// <summary>
/// IHttpClientFactory whose clients hit the app under test itself (the TestServer).
/// Workflow http_request nodes then make REAL calls to the in-app demo APIs —
/// a genuine API-to-API round trip instead of a scripted stub response.
/// </summary>
internal class LoopbackHttpClientFactory(Func<HttpClient> clientFactory) : System.Net.Http.IHttpClientFactory
{
    public HttpClient CreateClient(string name) => clientFactory();
}

/// <summary>
/// Verifies the seeded "Sample: API to API" workflow end to end: GET leads from the
/// demo CRM (System A) → loop over leads → map each lead → POST a kit reservation to
/// the demo Inventory (System B), with real HTTP calls between the systems.
/// </summary>
public class ApiToApiFlowTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _root = new();
    private readonly WebApplicationFactory<Program> _derived;
    private readonly HttpClient _client;

    public ApiToApiFlowTests()
    {
        WebApplicationFactory<Program>? derived = null;
        _derived = _root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Self:BaseUrl", "http://localhost:8000");
            // Fast dispatcher polling so queued runs complete quickly in tests.
            builder.UseSetting("Dispatcher:PollSeconds", "0.2");
            // Private InMemory store: same-name databases are shared process-wide,
            // and other hosts' dispatchers would claim this host's queued runs.
            builder.UseSetting("InMemory:DatabaseName", $"if-test-{Guid.NewGuid():N}");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<System.Net.Http.IHttpClientFactory>();
                // Lazy: the derived factory only exists after this lambda runs, and
                // CreateClient is only called later, during an actual workflow run.
                services.AddSingleton<System.Net.Http.IHttpClientFactory>(
                    new LoopbackHttpClientFactory(() => derived!.CreateDefaultClient()));
            });
        });
        derived = _derived;
        _client = _derived.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _derived.Dispose();
        _root.Dispose();
    }

    [Fact]
    public async Task Seeded_api_to_api_workflow_pulls_from_crm_and_pushes_to_inventory()
    {
        // Deterministic demo state: one lead in the CRM, full inventory stock.
        await _client.PostAsync("/demo/crm/reset", null);
        await _client.PostAsync("/demo/inventory/reset", null);
        var lead = await _client.PostAsJsonAsync("/demo/crm/leads",
            new { name = "Grace Hopper", company = "Eptura" });
        Assert.Equal(HttpStatusCode.OK, lead.StatusCode);

        // Find the seeded API-to-API example.
        var list = await _client.GetFromJsonAsync<JsonArray>("/api/workflows");
        var sample = list!.First(w => w!["name"]!.GetValue<string>().Contains("API to API"));
        var id = sample!["id"]!.GetValue<string>();

        // Manual run queues (202), the dispatcher executes: GET CRM leads → loop → map → POST reserve.
        var since = DateTimeOffset.UtcNow;
        var run = await _client.PostAsJsonAsync($"/api/workflows/{id}/run", new { });
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
        var runDto = await RunPolling.WaitForRunAsync(_client, Guid.Parse(id!), since);
        Assert.Equal("success", runDto["status"]!.GetValue<string>());

        // The run output is the loop result: one lead iterated.
        Assert.Equal(1, runDto["output"]!["count"]!.GetValue<int>());

        // The pull step really fetched the CRM list (real HTTP to the in-app demo API).
        var fetchStep = runDto["steps"]!.AsArray().First(s => s!["nodeId"]!.GetValue<string>() == "fetch-leads");
        Assert.Equal("success", fetchStep!["status"]!.GetValue<string>());
        Assert.Equal(200, fetchStep["output"]!["status"]!.GetValue<int>());
        Assert.Equal(1, fetchStep["output"]!["body"]!.AsArray().Count);

        // The mapping used the real lead fields.
        var mapStep = runDto["steps"]!.AsArray().First(s => s!["nodeId"]!.GetValue<string>() == "map-lead");
        Assert.Equal("Grace Hopper", mapStep!["output"]!["reservedFor"]!.GetValue<string>());

        // The push step reserved exactly one kit (100 - 1 = 99 remaining).
        var reserveStep = runDto["steps"]!.AsArray().First(s => s!["nodeId"]!.GetValue<string>() == "reserve-kit");
        Assert.Equal("WIDGET-1", reserveStep!["output"]!["body"]!["sku"]!.GetValue<string>());
        Assert.Equal(99, reserveStep["output"]!["body"]!["remaining"]!.GetValue<int>());

        // System B really changed: the inventory stock was decremented by the flow.
        var items = await _client.GetFromJsonAsync<JsonArray>("/demo/inventory/items");
        var widget = items!.First(i => i!["sku"]!.GetValue<string>() == "WIDGET-1");
        Assert.Equal(99, widget!["stock"]!.GetValue<int>());
    }

    [Fact]
    public async Task Seeded_api_to_api_workflow_is_idempotent_on_empty_crm()
    {
        // An empty System A is not an error: the loop iterates zero items, nothing is pushed.
        await _client.PostAsync("/demo/crm/reset", null);
        await _client.PostAsync("/demo/inventory/reset", null);

        var list = await _client.GetFromJsonAsync<JsonArray>("/api/workflows");
        var sample = list!.First(w => w!["name"]!.GetValue<string>().Contains("API to API"));
        var id = sample!["id"]!.GetValue<string>();

        var since = DateTimeOffset.UtcNow;
        var run = await _client.PostAsJsonAsync($"/api/workflows/{id}/run", new { });
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
        var runDto = await RunPolling.WaitForRunAsync(_client, Guid.Parse(id!), since);
        Assert.Equal("success", runDto["status"]!.GetValue<string>());
        Assert.Equal(0, runDto["output"]!["count"]!.GetValue<int>());

        // No reservation happened: stock untouched.
        var items = await _client.GetFromJsonAsync<JsonArray>("/demo/inventory/items");
        var widget = items!.First(i => i!["sku"]!.GetValue<string>() == "WIDGET-1");
        Assert.Equal(100, widget!["stock"]!.GetValue<int>());
    }
}
