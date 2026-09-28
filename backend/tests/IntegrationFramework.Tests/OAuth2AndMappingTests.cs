using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using IntegrationFramework.Core.Connectors;
using IntegrationFramework.Core.Engine;
using IntegrationFramework.Core.Entities;
using Xunit;

namespace IntegrationFramework.Tests;

public class OAuth2TokenManagerTests
{
    private static Connection Oauth2Connection(JsonObject authConfig) => new()
    {
        Name = "crm-oauth",
        Kind = "http",
        AuthType = "oauth2",
        AuthConfigJson = authConfig.ToJsonString()
    };

    [Fact]
    public async Task Client_credentials_sends_expected_form_and_returns_token()
    {
        List<string?>? capturedBodies = null;
        var factory = ScriptedHttpClientFactory.Responder(request =>
        {
            capturedBodies = [request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"tok-1","expires_in":3600}""", Encoding.UTF8, "application/json")
            };
        });
        var manager = new OAuth2TokenManager(factory);

        Environment.SetEnvironmentVariable("IF_TEST_OAUTH_SECRET", "super-secret");
        try
        {
            var connection = Oauth2Connection(new JsonObject
            {
                ["token_url"] = "https://idp.example.com/oauth/token",
                ["client_id"] = "client-1",
                ["client_secret_env"] = "IF_TEST_OAUTH_SECRET",
                ["scope"] = "read:leads write:leads"
            });
            var token = await manager.GetAccessTokenAsync(connection);

            Assert.Equal("tok-1", token);
            var form = capturedBodies![0];
            Assert.NotNull(form);
            var fields = form.Split('&');
            Assert.Contains(fields, f => f == "grant_type=client_credentials");
            Assert.Contains(fields, f => f == "client_id=client-1");
            Assert.Contains(fields, f => Uri.UnescapeDataString(f) == "client_secret=super-secret");
            // FormUrlEncodedContent encodes ':' as %3A and spaces as '+'.
            Assert.Contains(fields, f => f.StartsWith("scope=") &&
                Uri.UnescapeDataString(f[6..].Replace('+', ' ')) == "read:leads write:leads");
        }
        finally
        {
            Environment.SetEnvironmentVariable("IF_TEST_OAUTH_SECRET", null);
        }
    }

    [Fact]
    public async Task Tokens_are_cached_per_connection()
    {
        var tokenRequests = 0;
        var factory = ScriptedHttpClientFactory.Responder(_ =>
        {
            Interlocked.Increment(ref tokenRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"tok-cache","expires_in":3600}""", Encoding.UTF8, "application/json")
            };
        });
        var manager = new OAuth2TokenManager(factory);
        var connection = Oauth2Connection(new JsonObject
        {
            ["token_url"] = "https://idp.example.com/oauth/token",
            ["client_id"] = "client-1"
        });

        var first = await manager.GetAccessTokenAsync(connection);
        var second = await manager.GetAccessTokenAsync(connection);

        Assert.Equal("tok-cache", first);
        Assert.Equal("tok-cache", second);
        Assert.Equal(1, tokenRequests); // second call served from cache
    }

    [Fact]
    public async Task Near_expiry_tokens_are_refreshed()
    {
        var tokenRequests = 0;
        var factory = ScriptedHttpClientFactory.Responder(_ =>
        {
            var n = Interlocked.Increment(ref tokenRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"access_token":"tok-{{n}}","expires_in":30}""", Encoding.UTF8, "application/json")
            };
        });
        var manager = new OAuth2TokenManager(factory);
        var connection = Oauth2Connection(new JsonObject
        {
            ["token_url"] = "https://idp.example.com/oauth/token",
            ["client_id"] = "client-1"
        });

        // expires_in=30 minus the 60s margin clamps to a 1s cache lifetime.
        var first = await manager.GetAccessTokenAsync(connection);
        await Task.Delay(1100);
        var second = await manager.GetAccessTokenAsync(connection);

        Assert.Equal("tok-1", first);
        Assert.Equal("tok-2", second);
        Assert.Equal(2, tokenRequests);
    }

    [Fact]
    public async Task Refresh_token_grant_sends_refresh_token()
    {
        string? capturedBody = null;
        var factory = ScriptedHttpClientFactory.Responder(request =>
        {
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"tok-r","expires_in":3600,"refresh_token":"next-rt"}""", Encoding.UTF8, "application/json")
            };
        });
        var manager = new OAuth2TokenManager(factory);

        Environment.SetEnvironmentVariable("IF_TEST_OAUTH_RT", "refresh-me");
        try
        {
            var connection = Oauth2Connection(new JsonObject
            {
                ["grant_type"] = "refresh_token",
                ["token_url"] = "https://idp.example.com/oauth/token",
                ["client_id"] = "client-1",
                ["refresh_token_env"] = "IF_TEST_OAUTH_RT"
            });
            var token = await manager.GetAccessTokenAsync(connection);

            Assert.Equal("tok-r", token);
            Assert.NotNull(capturedBody);
            var fields = capturedBody.Split('&');
            Assert.Contains(fields, f => f == "grant_type=refresh_token");
            Assert.Contains(fields, f => Uri.UnescapeDataString(f) == "refresh_token=refresh-me");
        }
        finally
        {
            Environment.SetEnvironmentVariable("IF_TEST_OAUTH_RT", null);
        }
    }

    [Fact]
    public async Task Token_endpoint_failure_raises_informative_error()
    {
        var factory = ScriptedHttpClientFactory.Responder(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_client"}""", Encoding.UTF8, "application/json")
        });
        var manager = new OAuth2TokenManager(factory);
        var connection = Oauth2Connection(new JsonObject
        {
            ["token_url"] = "https://idp.example.com/oauth/token",
            ["client_id"] = "client-1"
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetAccessTokenAsync(connection));
        Assert.Contains("400", ex.Message);
        Assert.Contains("invalid_client", ex.Message);
    }

    [Fact]
    public async Task Missing_token_url_or_client_id_fails_fast()
    {
        var factory = ScriptedHttpClientFactory.Responder(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var manager = new OAuth2TokenManager(factory);

        var noUrl = Oauth2Connection(new JsonObject { ["client_id"] = "c" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetAccessTokenAsync(noUrl));

        var noClientId = Oauth2Connection(new JsonObject { ["token_url"] = "https://idp/oauth/token" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetAccessTokenAsync(noClientId));
    }
}

public class EntityMappingApplierTests
{
    private static readonly string MappingJson = new JsonObject
    {
        ["fields"] = new JsonArray(
            new JsonObject { ["source"] = "$.input.name", ["target"] = "customer_name", ["required"] = true, ["transforms"] = new JsonArray("trim", "upper") },
            new JsonObject { ["source"] = "email", ["target"] = "email", ["required"] = true },
            new JsonObject { ["source"] = "tier", ["target"] = "tier", ["default"] = "standard" },
            new JsonObject { ["source"] = "$.input.value", ["target"] = "value" })
    }.ToJsonString();

    private static readonly string RulesJson = new JsonObject
    {
        ["rules"] = new JsonArray(
            new JsonObject { ["field"] = "email", ["type"] = "string", ["required"] = true, ["pattern"] = "^[^@]+@[^@]+$" },
            new JsonObject { ["field"] = "customer_name", ["type"] = "string", ["minLength"] = 2, ["maxLength"] = 20 },
            new JsonObject { ["field"] = "value", ["type"] = "number", ["min"] = 0, ["max"] = 100 })
    }.ToJsonString();

    [Fact]
    public void Maps_run_context_paths_relative_paths_and_defaults()
    {
        var runContext = new RunContext(new JsonObject { ["name"] = "  ada  ", ["value"] = 42 });
        var source = new JsonObject { ["email"] = "ada@example.com" };

        var errors = EntityMappingApplier.ApplyFieldMappings(MappingJson, source, runContext, out var mapped);

        Assert.Empty(errors);
        Assert.Equal("ADA", mapped["customer_name"]!.GetValue<string>());
        Assert.Equal("ada@example.com", mapped["email"]!.GetValue<string>());
        Assert.Equal("standard", mapped["tier"]!.GetValue<string>());
        Assert.Equal(42, mapped["value"]!.GetValue<int>());
    }

    [Fact]
    public void Missing_required_source_is_reported_optional_missing_is_skipped()
    {
        var runContext = new RunContext(new JsonObject()); // no name, no value
        var source = new JsonObject { ["email"] = "ada@example.com" };

        var errors = EntityMappingApplier.ApplyFieldMappings(MappingJson, source, runContext, out var mapped);

        // The required "name" source is missing → reported; optional tier/value are skipped.
        Assert.Contains(errors, e => e.Contains("customer_name") && e.Contains("$.input.name"));
        Assert.False(mapped.ContainsKey("customer_name"));
        Assert.Equal("standard", mapped["tier"]!.GetValue<string>()); // default applied
    }

    [Fact]
    public void Validation_rules_report_all_violations()
    {
        var mapped = new JsonObject
        {
            ["customer_name"] = new string('x', 25), // too long
            ["email"] = "not-an-email",             // pattern violation
            ["value"] = 999                          // out of range
        };

        var errors = EntityMappingApplier.ValidateRules(mapped, RulesJson);

        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Contains("customer_name") && e.Contains("at most 20"));
        Assert.Contains(errors, e => e.Contains("email") && e.Contains("pattern"));
        Assert.Contains(errors, e => e.Contains("value") && e.Contains("at most 100"));
    }

    [Fact]
    public void Required_rule_violation_for_missing_field()
    {
        var mapped = new JsonObject { ["customer_name"] = "ok" };
        var errors = EntityMappingApplier.ValidateRules(mapped, RulesJson);
        Assert.Contains(errors, e => e.Contains("email") && e.Contains("required"));
    }

    [Fact]
    public void Type_rules_catch_wrong_types()
    {
        var mapped = new JsonObject { ["email"] = "ada@example.com", ["value"] = "not-a-number" };
        var errors = EntityMappingApplier.ValidateRules(mapped, RulesJson);
        Assert.Contains(errors, e => e.Contains("value") && e.Contains("must be a number"));
    }

    [Fact]
    public void Empty_rules_json_yields_no_errors()
    {
        var mapped = new JsonObject { ["a"] = 1 };
        Assert.Empty(EntityMappingApplier.ValidateRules(mapped, null));
        Assert.Empty(EntityMappingApplier.ValidateRules(mapped, ""));
        Assert.Single(EntityMappingApplier.ValidateRules(mapped, "{\"rules\": \"nope\"}"));
    }
}
