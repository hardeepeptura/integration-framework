using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Entities;
using IntegrationFramework.Worker;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IntegrationFramework.Tests;

/// <summary>
/// Verifies the retention service: old step runs / webhook events / workflow runs
/// are purged in the configured windows, fresh data is kept, and configured 0
/// disables a window. Hosted services are parked (hour-scale intervals) and
/// retention ticks are driven manually.
/// </summary>
public class RetentionTests
{
    private static WebApplicationFactory<Program> CreateFactory(params (string key, string value)[] settings)
    {
        var root = new WebApplicationFactory<Program>();
        var derived = root.WithWebHostBuilder(builder =>
        {
            // Park the hosted dispatcher and scheduler; retention runs at startup
            // on an empty store and then sleeps, so ticks below are manual.
            builder.UseSetting("Dispatcher:PollSeconds", "3600");
            // Private InMemory store: same-name databases are shared process-wide,
            // and other hosts' data would bleed into these exact counts.
            builder.UseSetting("InMemory:DatabaseName", $"if-test-{Guid.NewGuid():N}");
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
        });
        return derived;
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> factory, DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();

        var workflow = new Workflow { Name = $"retention-wf-{Guid.NewGuid():N}" };
        db.Workflows.Add(workflow);

        WorkflowRun Run(DateTimeOffset started) => new()
        {
            WorkflowId = workflow.Id,
            Status = "success",
            StartedAt = started,
            FinishedAt = started.AddSeconds(1)
        };

        var oldRun = Run(now.AddDays(-100));
        var freshRun = Run(now);
        var ancientRun = Run(now.AddDays(-400));
        db.WorkflowRuns.AddRange(oldRun, freshRun, ancientRun);
        db.StepRuns.AddRange(
            new StepRun { RunId = oldRun.Id, NodeId = "n1", NodeType = "transform", Status = "success", StartedAt = now.AddDays(-100) },
            new StepRun { RunId = freshRun.Id, NodeId = "n1", NodeType = "transform", Status = "success", StartedAt = now },
            new StepRun { RunId = ancientRun.Id, NodeId = "n1", NodeType = "transform", Status = "success", StartedAt = now.AddDays(-400) });

        db.WebhookEvents.AddRange(
            new WebhookEvent { WorkflowId = workflow.Id, Status = "succeeded", ReceivedAt = now.AddDays(-100), BodyJson = "{}" },
            new WebhookEvent { WorkflowId = workflow.Id, Status = "succeeded", ReceivedAt = now, BodyJson = "{}" });

        await db.SaveChangesAsync();
    }

    // AddHostedService<T> does not register the concrete type on .NET 10, so the
    // tests construct the worker services directly against the host's providers.
    private static RetentionBackgroundService CreateRetention(WebApplicationFactory<Program> factory) => new(
        factory.Services.GetRequiredService<IServiceScopeFactory>(),
        factory.Services.GetRequiredService<ILogger<RetentionBackgroundService>>(),
        factory.Services.GetRequiredService<IConfiguration>());

    private static async Task<(int runs, int steps, int events)> CountsAsync(WebApplicationFactory<Program> factory, Guid workflowId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var runs = await db.WorkflowRuns.CountAsync(r => r.WorkflowId == workflowId);
        var steps = await db.StepRuns.CountAsync();
        var events = await db.WebhookEvents.CountAsync(e => e.WorkflowId == workflowId);
        return (runs, steps, events);
    }

    [Fact]
    public async Task Default_windows_purge_old_data_and_keep_fresh()
    {
        using var factory = CreateFactory();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(factory, now);

        Guid workflowId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
            workflowId = db.Workflows
                .OrderByDescending(w => w.CreatedAt)
                .First(w => w.Name.StartsWith("retention-wf-")).Id;
        }

        var retention = CreateRetention(factory);
        var deleted = await retention.TickAsync(now, CancellationToken.None);
        Assert.True(deleted >= 4); // 2 old steps + 1 ancient run (cascade) + 1 old event

        var counts = await CountsAsync(factory, workflowId);
        Assert.Equal((2, 1, 1), counts); // old(100d) + fresh runs kept; fresh step; fresh event
    }

    [Fact]
    public async Task Configured_zero_disables_every_window()
    {
        using var factory = CreateFactory(
            ("Retention:StepRunDays", "0"),
            ("Retention:WebhookEventDays", "0"),
            ("Retention:WorkflowRunDays", "0"));
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(factory, now);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var workflow = db.Workflows.OrderByDescending(w => w.CreatedAt).First(w => w.Name.StartsWith("retention-wf-"));

        var retention = CreateRetention(factory);
        var deleted = await retention.TickAsync(now, CancellationToken.None);
        Assert.Equal(0, deleted);

        var counts = await CountsAsync(factory, workflow.Id);
        Assert.Equal((3, 3, 2), counts); // nothing purged
    }
}
