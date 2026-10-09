using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Entities;
using IntegrationFramework.Core.Engine;
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
/// Verifies the durable run queue end to end: webhook enqueue → dispatcher claim →
/// execute → link run + webhook event; visibility-timeout crash recovery; and the
/// schedule dedupe key that makes multiple scheduler replicas safe. The hosted
/// dispatcher is parked (Dispatcher:PollSeconds=3600) and ticks are driven manually.
/// </summary>
public class DispatcherTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _root = new();
    private readonly WebApplicationFactory<Program> _derived;
    private readonly HttpClient _client;

    public DispatcherTests()
    {
        WebApplicationFactory<Program>? derived = null;
        _derived = _root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Self:BaseUrl", "http://localhost:8000");
            // Park the hosted dispatcher: ticks are driven manually in these tests.
            builder.UseSetting("Dispatcher:PollSeconds", "3600");
            // Private InMemory store: same-name databases are shared process-wide,
            // and other hosts' dispatchers would claim this host's queued runs.
            builder.UseSetting("InMemory:DatabaseName", $"if-test-{Guid.NewGuid():N}");
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

    // AddHostedService<T> does not register the concrete type on .NET 10, so the
    // tests construct the worker services directly against the host's providers.
    private RunDispatcherBackgroundService CreateDispatcher() => new(
        _derived.Services.GetRequiredService<IServiceScopeFactory>(),
        _derived.Services.GetRequiredService<ILogger<RunDispatcherBackgroundService>>(),
        _derived.Services.GetRequiredService<IConfiguration>());

    private SchedulerBackgroundService CreateScheduler() => new(
        _derived.Services.GetRequiredService<IServiceScopeFactory>(),
        _derived.Services.GetRequiredService<ILogger<SchedulerBackgroundService>>(),
        _derived.Services.GetRequiredService<IConfiguration>());

    private async Task<List<RunQueueItem>> QueueItemsAsync()
    {
        using var scope = _derived.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        return await db.RunQueueItems.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task Webhook_queue_item_is_claimed_executed_once_and_links_event_and_run()
    {
        await _client.PostAsync("/demo/inventory/reset", null);
        var workflows = await _client.GetFromJsonAsync<JsonArray>("/api/workflows");
        var sample = workflows!.First(w => w!["name"]!.GetValue<string>() == WorkflowSeeder.SampleWorkflowName);
        var sampleId = sample!["id"]!.GetValue<string>();

        // 202-queued; nothing executes yet (the hosted dispatcher is parked).
        var post = await _client.PostAsJsonAsync($"/webhook/{sampleId}",
            new { sku = "WIDGET-1", quantity = 2, name = "Dispatch" });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var postDto = await post.Content.ReadFromJsonAsync<JsonObject>();
        var eventId = postDto!["eventId"]!.GetValue<string>();

        // No run exists yet; the event is still "received".
        Assert.Empty((await QueueItemsAsync()).Where(q => q.Status != RunQueueItem.StatusQueued));

        var dispatcher = CreateDispatcher();
        var claimed = await dispatcher.TickAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(1, claimed);

        // The event flipped to succeeded and links its run; the run succeeded against
        // the real demo inventory through the loopback factory (100 - 2 = 98).
        var evt = await _client.GetFromJsonAsync<JsonObject>($"/api/webhook-events/{eventId}");
        Assert.Equal("succeeded", evt!["status"]!.GetValue<string>());
        var runId = evt["runId"]!.GetValue<string>();
        var run = await _client.GetFromJsonAsync<JsonObject>($"/api/runs/{runId}");
        Assert.Equal("success", run!["status"]!.GetValue<string>());
        Assert.Equal(98, run["output"]!["body"]!["remaining"]!.GetValue<int>());

        // The queue item finished exactly once; a second tick claims nothing.
        var item = (await QueueItemsAsync()).Single(q => q.WebhookEventId == Guid.Parse(eventId));
        Assert.Equal(RunQueueItem.StatusCompleted, item.Status);
        Assert.Equal(1, item.Attempts);
        Assert.NotNull(item.RunId);
        Assert.Equal(0, await dispatcher.TickAsync(DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task Claimed_item_past_visibility_timeout_is_reclaimed_and_executed()
    {
        using (var scope = _derived.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
            var workflow = db.Workflows.First(w => w.Name == WorkflowSeeder.SampleWorkflowName);
            // A worker claimed this and died: visibility has long expired.
            db.RunQueueItems.Add(new RunQueueItem
            {
                WorkflowId = workflow.Id,
                InputJson = new JsonObject { ["sku"] = "GIZMO-2", ["quantity"] = 1 }.ToJsonString(),
                TriggerType = "manual",
                DedupeKey = "run:stale-claim",
                Status = RunQueueItem.StatusClaimed,
                Attempts = 1,
                ClaimedUntil = DateTimeOffset.UtcNow.AddHours(-1)
            });
            db.SaveChanges();
        }

        var dispatcher = CreateDispatcher();
        var claimed = await dispatcher.TickAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(1, claimed);

        var item = (await QueueItemsAsync()).Single(q => q.DedupeKey == "run:stale-claim");
        Assert.Equal(RunQueueItem.StatusCompleted, item.Status);
        Assert.Equal(2, item.Attempts); // once by the dead worker, once by the reclaim
        Assert.NotNull(item.RunId);
    }

    [Fact]
    public async Task Scheduler_replicas_cannot_double_fire_the_same_interval()
    {
        var create = await _client.PostAsJsonAsync("/api/workflows", new
        {
            name = "sched-dedupe",
            graph = new JsonObject
            {
                ["nodes"] = new JsonArray(new JsonObject
                {
                    ["id"] = "trigger",
                    ["type"] = "trigger",
                    ["config"] = new JsonObject { ["trigger"] = "schedule", ["intervalSeconds"] = 3600 }
                })
            }
        });
        var wf = await create.Content.ReadFromJsonAsync<JsonObject>();
        var wfId = Guid.Parse(wf!["id"]!.GetValue<string>());

        // Two scheduler service instances (two replicas) tick at the same moment.
        var scheduler1 = CreateScheduler();
        var scheduler2 = CreateScheduler();
        var now = DateTimeOffset.UtcNow;
        await scheduler1.TickAsync(now, CancellationToken.None);
        await scheduler2.TickAsync(now, CancellationToken.None);

        using var scope = _derived.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var items = await db.RunQueueItems.AsNoTracking().Where(q => q.WorkflowId == wfId).ToListAsync();
        var scheduled = items.Where(q => q.TriggerType == "schedule").ToList();
        Assert.Single(scheduled); // the dedupe key admits exactly one per interval
        Assert.Equal(RunQueueItem.StatusQueued, scheduled[0].Status);
        Assert.StartsWith($"schedule:{wfId}:", scheduled[0].DedupeKey);
    }
}
