using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntegrationFramework.Tests;

/// <summary>
/// Verifies the seeded OAuth2 example end to end: the engine fetches a
/// client-credentials token from the demo authorization server via the seeded
/// connection, pulls orders from the Bearer-secured demo API, maps each order,
/// and pushes reservations to the demo Inventory — all with REAL HTTP calls
/// through the loopback factory.
/// </summary>
public class OAuth2FlowTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _root = new();
    private readonly WebApplicationFactory<Program> _derived;
    private readonly HttpClient _client;

    public OAuth2FlowTests()
    {
        WebApplicationFactory<Program>? derived = null;
        _derived = _root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Self:BaseUrl", "http://localhost:8000");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<System.Net.Http.IHttpClientFactory>();
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

    private static HttpContent FormContent(params (string key, string value)[] fields)
    {
        var pairs = fields.Select(f => $"{Uri.EscapeDataString(f.key)}={Uri.EscapeDataString(f.value)}");
        return new StringContent(string.Join("&", pairs), Encoding.UTF8, "application/x-www-form-urlencoded");
    }

    [Fact]
    public async Task Seeded_oauth2_workflow_fetches_token_pulls_secure_orders_and_reserves()
    {
        await _client.PostAsync("/demo/oauth2/reset", null);
        await _client.PostAsync("/demo/inventory/reset", null);

        var list = await _client.GetFromJsonAsync<JsonArray>("/api/workflows");
        var sample = list!.First(w => w!["name"]!.GetValue<string>().Contains("OAuth2"));
        var id = sample!["id"]!.GetValue<string>();

        var run = await _client.PostAsJsonAsync($"/api/workflows/{id}/run", new { });
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var runDto = await run.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("success", runDto!["status"]!.GetValue<string>());

        // Two orders were pulled and processed.
        Assert.Equal(2, runDto["output"]!["count"]!.GetValue<int>());

        // The pull step authenticated with a real Bearer token and got the order list.
        var fetchStep = runDto["steps"]!.AsArray().First(s => s!["nodeId"]!.GetValue<string>() == "fetch-orders");
        Assert.Equal("success", fetchStep!["status"]!.GetValue<string>());
        Assert.Equal(200, fetchStep["output"]!["status"]!.GetValue<int>());
        Assert.Equal(2, fetchStep["output"]!["body"]!.AsArray().Count);

        // Both reservations landed in the inventory (2 x WIDGET-1, 1 x GIZMO-2).
        var items = await _client.GetFromJsonAsync<JsonArray>("/demo/inventory/items");
        Assert.Equal(98, items!.First(i => i!["sku"]!.GetValue<string>() == "WIDGET-1")!["stock"]!.GetValue<int>());
        Assert.Equal(49, items!.First(i => i!["sku"]!.GetValue<string>() == "GIZMO-2")!["stock"]!.GetValue<int>());
    }

    [Fact]
    public async Task Demo_token_endpoint_rejects_wrong_credentials()
    {
        var response = await _client.PostAsync("/demo/oauth2/token",
            FormContent(
                ("grant_type", "client_credentials"),
                ("client_id", "demo-client"),
                ("client_secret", "WRONG")));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("invalid_client", body!["error"]!.GetValue<string>());

        var badGrant = await _client.PostAsync("/demo/oauth2/token",
            FormContent(("grant_type", "password"), ("client_id", "demo-client"), ("client_secret", "demo-secret")));
        Assert.Equal(HttpStatusCode.BadRequest, badGrant.StatusCode);
    }

    [Fact]
    public async Task Secure_orders_requires_a_valid_bearer_token()
    {
        // No token → 401.
        var anonymous = await _client.GetAsync("/demo/secure/orders");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // Bogus token → 401.
        using var bogus = new HttpRequestMessage(HttpMethod.Get, "/demo/secure/orders");
        bogus.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-real-token");
        var bogusResponse = await _client.SendAsync(bogus);
        Assert.Equal(HttpStatusCode.Unauthorized, bogusResponse.StatusCode);

        // Real token issued by the demo authorization server → 200 with the orders.
        var tokenResponse = await _client.PostAsync("/demo/oauth2/token",
            FormContent(
                ("grant_type", "client_credentials"),
                ("client_id", "demo-client"),
                ("client_secret", "demo-secret")));
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var token = await tokenResponse.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("Bearer", token!["token_type"]!.GetValue<string>());

        using var authorized = new HttpRequestMessage(HttpMethod.Get, "/demo/secure/orders");
        authorized.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token["access_token"]!.GetValue<string>());
        var ordersResponse = await _client.SendAsync(authorized);
        Assert.Equal(HttpStatusCode.OK, ordersResponse.StatusCode);
        var orders = await ordersResponse.Content.ReadFromJsonAsync<JsonArray>();
        Assert.Equal(2, orders!.Count);
    }
}
