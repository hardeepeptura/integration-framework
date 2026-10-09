using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntegrationFramework.Tests;

/// <summary>
/// Verifies the paged, searchable runs list: page/pageSize/total math, ordering,
/// and search matching (workflow name, status, error text, exact run id).
/// Runs anonymous (SSO-off path) with a private InMemory store.
/// </summary>
public class RunsSearchPaginationTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _root = new();
    private readonly WebApplicationFactory<Program> _derived;
    private readonly HttpClient _client;

    public RunsSearchPaginationTests()
    {
        _derived = _root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Self:BaseUrl", "http://localhost:8000");
            builder.UseSetting("Dispatcher:PollSeconds", "3600"); // no queue traffic in these tests
            builder.UseSetting("InMemory:DatabaseName", $"if-test-{Guid.NewGuid():N}");
        });
        _client = _derived.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _derived.Dispose();
        _root.Dispose();
    }

    private async Task<(Guid alphaId, Guid betaId, Guid failedRunId)> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = _derived.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();

        var alpha = new Workflow { Name = "alpha-sync" };
        var beta = new Workflow { Name = "beta-reports" };
        db.Workflows.AddRange(alpha, beta);

        WorkflowRun Run(Guid wf, string status, string? error, int minutesAgo) => new()
        {
            WorkflowId = wf,
            Status = status,
            Error = error,
            StartedAt = now.AddMinutes(-minutesAgo),
            FinishedAt = now.AddMinutes(-minutesAgo).AddSeconds(1)
        };

        var failed = Run(alpha.Id, "failed", "HTTP 503 Service Unavailable from partner", 1);
        // 5 runs total: 3 alpha (2 success + 1 failed) + 2 beta.
        db.WorkflowRuns.AddRange(
            failed,
            Run(alpha.Id, "success", null, 2),
            Run(alpha.Id, "success", null, 3),
            Run(beta.Id, "success", null, 4),
            Run(beta.Id, "success", null, 5));
        await db.SaveChangesAsync();
        return (alpha.Id, beta.Id, failed.Id);
    }

    private static async Task<JsonObject> ListAsync(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<JsonObject>($"/api/runs{query}"))!;

    [Fact]
    public async Task List_is_paged_newest_first_with_totals()
    {
        await SeedAsync();

        var page1 = await ListAsync(_client, "?page=1&pageSize=2");
        Assert.Equal(2, page1["items"]!.AsArray().Count);
        Assert.Equal(5, page1["total"]!.GetValue<int>());
        Assert.Equal(1, page1["page"]!.GetValue<int>());
        Assert.Equal(2, page1["pageSize"]!.GetValue<int>());

        // Newest first: the failed run (1 minute ago) leads.
        Assert.Equal("failed", page1["items"]![0]!["status"]!.GetValue<string>());

        var page3 = await ListAsync(_client, "?page=3&pageSize=2");
        Assert.Single(page3["items"]!.AsArray());
        Assert.Equal(5, page3["total"]!.GetValue<int>());

        // Beyond the last page: empty items, same total.
        var page6 = await ListAsync(_client, "?page=6&pageSize=2");
        Assert.Empty(page6["items"]!.AsArray());
        Assert.Equal(5, page6["total"]!.GetValue<int>());

        // Defaults: page 1, pageSize 100.
        var defaults = await ListAsync(_client, "");
        Assert.Equal(5, defaults["items"]!.AsArray().Count);
        Assert.Equal(100, defaults["pageSize"]!.GetValue<int>());
    }

    [Fact]
    public async Task Search_matches_workflow_name_status_and_error()
    {
        var (alphaId, betaId, _) = await SeedAsync();

        // Workflow name (case-insensitive).
        var byName = await ListAsync(_client, "?search=ALPHA");
        Assert.Equal(3, byName["total"]!.GetValue<int>());
        Assert.All(byName["items"]!.AsArray(), r => Assert.Equal(alphaId, Guid.Parse(r!["workflowId"]!.GetValue<string>())));

        // Status term.
        var byStatus = await ListAsync(_client, "?search=failed");
        Assert.Equal(1, byStatus["total"]!.GetValue<int>());
        Assert.Equal("failed", byStatus["items"]![0]!["status"]!.GetValue<string>());

        // Error text.
        var byError = await ListAsync(_client, "?search=503");
        Assert.Equal(1, byError["total"]!.GetValue<int>());

        // Nothing matches.
        var none = await ListAsync(_client, "?search=does-not-exist");
        Assert.Equal(0, none["total"]!.GetValue<int>());
        Assert.Empty(none["items"]!.AsArray());

        // Search + pagination combine: beta has 2 runs, page 1 of size 1.
        var paged = await ListAsync(_client, "?search=beta-reports&page=1&pageSize=1");
        Assert.Single(paged["items"]!.AsArray());
        Assert.Equal(2, paged["total"]!.GetValue<int>());
        _ = betaId;
    }

    [Fact]
    public async Task Search_matches_an_exact_run_id()
    {
        var (_, _, failedRunId) = await SeedAsync();

        var byId = await ListAsync(_client, $"?search={failedRunId}");
        Assert.Equal(1, byId["total"]!.GetValue<int>());
        Assert.Equal(failedRunId, Guid.Parse(byId["items"]![0]!["id"]!.GetValue<string>()));

        // A random guid matches nothing.
        var none = await ListAsync(_client, $"?search={Guid.NewGuid()}");
        Assert.Equal(0, none["total"]!.GetValue<int>());
    }
}
