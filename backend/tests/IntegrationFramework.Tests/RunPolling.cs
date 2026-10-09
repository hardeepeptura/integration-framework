using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace IntegrationFramework.Tests;

/// <summary>
/// Polling helpers for the async-first run contract: trigger endpoints return
/// 202-queued; the worker dispatcher executes and flips run/event status. Tests
/// with fast dispatcher polling (Dispatcher:PollSeconds=0.2) use these to wait
/// for the finished artifacts.
/// </summary>
public static class RunPolling
{
    /// <summary>Waits for a run of the workflow started at/after `since`, then returns its full detail (steps included).</summary>
    public static async Task<JsonObject> WaitForRunAsync(
        HttpClient client, Guid workflowId, DateTimeOffset since, int attempts = 60, int delayMs = 250)
    {
        for (var i = 0; i < attempts; i++)
        {
            var page = await client.GetFromJsonAsync<JsonObject>(
                $"/api/runs?workflowId={workflowId}&page=1&pageSize=50");
            var runs = page?["items"] as JsonArray;
            var candidate = runs?
                .Where(r => DateTimeOffset.TryParse(r!["startedAt"]?.GetValue<string>(), out var t) && t >= since)
                .OrderByDescending(r => r!["startedAt"]!.GetValue<string>(), StringComparer.Ordinal)
                .FirstOrDefault();
            if (candidate is not null)
            {
                var id = candidate["id"]!.GetValue<string>();
                var detail = await client.GetFromJsonAsync<JsonObject>($"/api/runs/{id}");
                // Runs are persisted "running" BEFORE execution: wait for a terminal status
                // so the detail carries the final output + full step list.
                if (detail?["status"]?.GetValue<string>() is "success" or "failed")
                    return detail!;
            }
            await Task.Delay(delayMs);
        }
        throw new Xunit.Sdk.XunitException(
            $"No run for workflow {workflowId} appeared within {attempts * delayMs}ms — the dispatcher never executed it.");
    }

    /// <summary>Waits for a webhook delivery to reach a terminal status (succeeded/failed/rejected).</summary>
    public static async Task<JsonObject> WaitForEventAsync(
        HttpClient client, Guid eventId, int attempts = 60, int delayMs = 250)
    {
        for (var i = 0; i < attempts; i++)
        {
            var evt = await client.GetFromJsonAsync<JsonObject>($"/api/webhook-events/{eventId}");
            if (evt is not null)
            {
                var status = evt["status"]?.GetValue<string>();
                if (status is "succeeded" or "failed" or "rejected") return evt;
            }
            await Task.Delay(delayMs);
        }
        throw new Xunit.Sdk.XunitException(
            $"Webhook event {eventId} did not reach a terminal status within {attempts * delayMs}ms.");
    }
}
