using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntegrationFramework.Tests;

/// <summary>
/// Minimal repro: bare factory, no overrides. If this passes while ApiIntegrationTests fails,
/// the problem is in the TestFactory overrides; if it fails too, it is app-side.
/// </summary>
public class MinimalFactoryReproTests
{
    [Fact]
    public async Task Bare_factory_health_endpoint_works()
    {
        var factory = new WebApplicationFactory<Program>();
        try
        {
            var client = factory.CreateClient();
            var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal("ok", body!["status"]!.GetValue<string>());
        }
        finally
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task Bare_factory_with_workhostbuilder_still_starts()
    {
        var factory = new WebApplicationFactory<Program>();
        var derived = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Self:BaseUrl", "http://localhost:8000");
        });
        try
        {
            var client = derived.CreateClient();
            var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            derived.Dispose();
            factory.Dispose();
        }
    }

    [Fact]
    public async Task Bare_factory_with_configure_services_still_starts()
    {
        var factory = new WebApplicationFactory<Program>();
        var derived = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Self:BaseUrl", "http://localhost:8000");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<System.Net.Http.IHttpClientFactory>();
                services.AddSingleton<System.Net.Http.IHttpClientFactory>(
                    IntegrationFramework.Tests.ScriptedHttpClientFactory.Responder(
                        _ => new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{}")
                        }));
            });
        });
        try
        {
            var client = derived.CreateClient();
            var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            derived.Dispose();
            factory.Dispose();
        }
    }
}
