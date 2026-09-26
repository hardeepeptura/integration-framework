using System.Text.Json.Nodes;
using IntegrationFramework.Connectors;
using IntegrationFramework.Core.Connectors;
using IntegrationFramework.Core.Engine;
using IntegrationFramework.Core.Engine.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntegrationFramework.Tests;

/// <summary>IHttpClientFactory stub returning a client with a scripted handler.</summary>
internal class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
{
    public HttpRequestMessage? LastRequest { get; private set; }

    public HttpClient CreateClient(string name)
    {
        var captured = LastRequest;
        return new HttpClient(new StubHandler(r =>
        {
            LastRequest = r;
            return responder(r);
        }));
    }

    private class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}

public class HttpNodeTests : IDisposable
{
    private readonly ServiceProvider _provider = BuildProvider();

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("workflow-http");
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    private static NodeExecutionContext MakeContext(
        string nodeId, JsonObject config, RunContext runContext, IHttpClientFactory factory) =>
        new(nodeId, config, runContext, new ServiceCollection()
            .AddSingleton(factory)
            .BuildServiceProvider(), CancellationToken.None);

    [Fact]
    public async Task Posts_json_body_and_parses_response()
    {
        string? sentBody = null;
        HttpMethod? sentMethod = null;
        var stub = new StubHttpClientFactory(request =>
        {
            // Capture while the request is still alive; it is disposed when the node returns.
            sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            sentMethod = request.Method;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"reserved":2,"remaining":98}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
        });
        var node = new HttpRequestNode(stub);
        var runContext = new RunContext(JsonNode.Parse("""{"sku":"WIDGET-1","quantity":2}"""));
        var config = new JsonObject
        {
            ["url"] = "http://inventory.local/reserve",
            ["method"] = "POST",
            ["body"] = new JsonObject
            {
                ["sku"] = "$.input.sku",
                ["quantity"] = "$.input.quantity"
            }
        };

        var output = await node.ExecuteAsync(MakeContext("http-1", config, runContext, stub));

        Assert.Equal(200, output!["status"]!.GetValue<int>());
        Assert.True(output["ok"]!.GetValue<bool>());
        Assert.Equal(98, output["body"]!["remaining"]!.GetValue<int>());
        Assert.Contains("WIDGET-1", sentBody);
        Assert.Contains("2", sentBody);
        Assert.Equal(HttpMethod.Post, sentMethod);
    }

    [Fact]
    public async Task Non_2xx_response_raises_node_failure()
    {
        var stub = new StubHttpClientFactory(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":\"not found\"}")
            });
        var node = new HttpRequestNode(stub);
        var config = new JsonObject { ["url"] = "http://inventory.local/nope", ["method"] = "GET" };

        await Assert.ThrowsAsync<NodeExecutionException>(() =>
            node.ExecuteAsync(MakeContext("http-1", config, new RunContext(null), stub)));
    }

    [Fact]
    public async Task Missing_url_config_fails()
    {
        var stub = new StubHttpClientFactory(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var node = new HttpRequestNode(stub);
        var config = new JsonObject { ["method"] = "GET" };

        await Assert.ThrowsAsync<NodeExecutionException>(() =>
            node.ExecuteAsync(MakeContext("http-1", config, new RunContext(null), stub)));
    }

    [Fact]
    public async Task Interpolated_url_resolves_env()
    {
        var stub = new StubHttpClientFactory(request =>
        {
            Assert.Equal("http://localhost:8000/demo/inventory/reserve", request.RequestUri!.ToString());
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        });
        var node = new HttpRequestNode(stub);
        var runContext = new RunContext(null, new Dictionary<string, string> { ["self_base_url"] = "http://localhost:8000" });
        var config = new JsonObject
        {
            ["url"] = "{$.env.self_base_url}/demo/inventory/reserve",
            ["method"] = "POST",
            ["body"] = new JsonObject { ["sku"] = "WIDGET-1" }
        };

        var output = await node.ExecuteAsync(MakeContext("http-1", config, runContext, stub));
        Assert.Equal(200, output!["status"]!.GetValue<int>());
    }
}

public class HttpAuthApplierTests
{
    [Fact]
    public void Bearer_auth_reads_token_from_env()
    {
        const string varName = "IF_TEST_BEARER_TOKEN";
        Environment.SetEnvironmentVariable(varName, "tok-123");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://x");
            HttpAuthApplier.Apply(request, "bearer", new JsonObject { ["token_env"] = varName }.ToJsonString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("tok-123", request.Headers.Authorization!.Parameter);
        }
        finally { Environment.SetEnvironmentVariable(varName, null); }
    }

    [Fact]
    public void Api_key_auth_uses_custom_header()
    {
        const string varName = "IF_TEST_API_KEY";
        Environment.SetEnvironmentVariable(varName, "key-42");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://x");
            HttpAuthApplier.Apply(request, "api_key", new JsonObject
            {
                ["header_name"] = "X-Custom-Key",
                ["value_env"] = varName
            }.ToJsonString());
            Assert.True(request.Headers.TryGetValues("X-Custom-Key", out var values));
            Assert.Equal("key-42", values!.Single());
        }
        finally { Environment.SetEnvironmentVariable(varName, null); }
    }

    [Fact]
    public void Basic_auth_encodes_credentials()
    {
        const string varName = "IF_TEST_BASIC_PW";
        Environment.SetEnvironmentVariable(varName, "pw-secret");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://x");
            HttpAuthApplier.Apply(request, "basic", new JsonObject
            {
                ["username"] = "svc-user",
                ["password_env"] = varName
            }.ToJsonString());
            var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("svc-user:pw-secret"));
            Assert.Equal(expected, request.Headers.Authorization!.Parameter);
        }
        finally { Environment.SetEnvironmentVariable(varName, null); }
    }

    [Fact]
    public void Missing_env_var_fails_with_clear_error()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://x");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            HttpAuthApplier.Apply(request, "bearer", new JsonObject { ["token_env"] = "IF_DEFINITELY_NOT_SET_VAR" }.ToJsonString()));
        Assert.Contains("IF_DEFINITELY_NOT_SET_VAR", ex.Message);
    }

    [Fact]
    public void None_auth_adds_nothing()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://x");
        HttpAuthApplier.Apply(request, "none", null);
        Assert.Null(request.Headers.Authorization);
    }
}

public class MaskingTests
{
    [Fact]
    public void Masks_secret_values_but_keeps_env_names()
    {
        var masked = ConnectionMasker.MaskConfigJson(new JsonObject
        {
            ["header_name"] = "X-API-Key",
            ["value_env"] = "MY_API_KEY_ENV",
            ["value"] = "super-secret-123"
        }.ToJsonString());
        var obj = JsonNode.Parse(masked!)!.AsObject();
        Assert.Equal("X-API-Key", obj["header_name"]!.GetValue<string>());
        Assert.Equal("MY_API_KEY_ENV", obj["value_env"]!.GetValue<string>());
        Assert.Equal("********", obj["value"]!.GetValue<string>());
    }

    [Fact]
    public void Masks_db_password_literals()
    {
        var masked = ConnectionMasker.MaskConfigJson(new JsonObject
        {
            ["host"] = "db.internal",
            ["user"] = "svc",
            ["password"] = "P@ssw0rd!",
            ["password_env"] = "DB_PW"
        }.ToJsonString());
        var obj = JsonNode.Parse(masked!)!.AsObject();
        Assert.Equal("********", obj["password"]!.GetValue<string>());
        Assert.Equal("db.internal", obj["host"]!.GetValue<string>());
        Assert.Equal("svc", obj["user"]!.GetValue<string>());
        Assert.Equal("DB_PW", obj["password_env"]!.GetValue<string>());
    }

    [Fact]
    public void Null_or_empty_config_passes_through()
    {
        Assert.Null(ConnectionMasker.MaskConfigJson(null));
        Assert.Equal(string.Empty, ConnectionMasker.MaskConfigJson(string.Empty));
    }
}
