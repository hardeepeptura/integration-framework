using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntegrationFramework.Tests;

/// <summary>
/// Verifies the DB-side dashboard aggregation: totals, pass/fail rates, the
/// zero-filled bucket series (same semantics as before: uniform steps walked
/// back from "now"), busiest workflows, and range validation. Runs on the
/// InMemory provider (client-side grouping path); SQL Server uses the grouped
/// raw query with the same math.
/// </summary>
public class DashboardTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _root = new();
    private readonly WebApplicationFactory<Program> _derived;
    private readonly HttpClient _client;

    public DashboardTests()
    {
        _derived = _root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Self:BaseUrl", "http://localhost:8000");
            builder.UseSetting("Dispatcher:PollSeconds", "3600"); // no queue traffic in these tests
            // Private InMemory store: same-name databases are shared process-wide,
            // which would corrupt the exact count assertions.
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

    [Fact]
    public async Task Summary_aggregates_totals_rates_buckets_and_top_workflows()
    {
        var now = DateTimeOffset.UtcNow;
        var wfA = new Workflow { Name = "dash-wf-a" };
        var wfB = new Workflow { Name = "dash-wf-b" };
        using (var scope = _derived.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
            db.Workflows.AddRange(wfA, wfB);
            db.WorkflowRuns.AddRange(
                new WorkflowRun { WorkflowId = wfA.Id, Status = "success", StartedAt = now.AddMinutes(-10) },
                new WorkflowRun { WorkflowId = wfA.Id, Status = "failed", StartedAt = now.AddMinutes(-30) },
                new WorkflowRun { WorkflowId = wfB.Id, Status = "success", StartedAt = now.AddHours(-2) });
            db.SaveChanges();
        }

        var day = await _client.GetFromJsonAsync<JsonObject>("/api/dashboard/summary?range=24h");
        Assert.Equal(3, day!["totalRuns"]!.GetValue<int>());
        Assert.Equal(2, day["successRuns"]!.GetValue<int>());
        Assert.Equal(1, day["failedRuns"]!.GetValue<int>());
        Assert.Equal(66.7, day["successRate"]!.GetValue<double>(), 1);
        Assert.Equal(33.3, day["failureRate"]!.GetValue<double>(), 1);
        // Seeded samples (3) + dash-wf-a + dash-wf-b.
        Assert.Equal(5, day["workflowCount"]!.GetValue<int>());

        // Contiguous hourly grid over the window, all runs counted exactly once.
        var buckets = day["buckets"]!.AsArray();
        Assert.Equal(24, buckets.Count);
        Assert.Equal(3, buckets.Sum(b => b!["total"]!.GetValue<int>()));
        Assert.Equal(0, buckets[0]!["total"]!.GetValue<int>()); // zero-filled leading bucket

        // Busiest workflow first.
        Assert.Equal(wfA.Id, Guid.Parse(day["topWorkflows"]![0]!["workflowId"]!.GetValue<string>()));
        Assert.Equal(2, day["topWorkflows"]![0]!["total"]!.GetValue<int>());

        // Last hour window: the 2h-old run falls out; 12 five-minute buckets.
        var hour = await _client.GetFromJsonAsync<JsonObject>("/api/dashboard/summary?range=hour");
        Assert.Equal(2, hour!["totalRuns"]!.GetValue<int>());
        Assert.Equal(12, hour["buckets"]!.AsArray().Count);

        // Invalid range → 400.
        var bad = await _client.GetAsync("/api/dashboard/summary?range=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
