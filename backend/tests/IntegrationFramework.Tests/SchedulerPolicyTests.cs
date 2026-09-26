using System.Text.Json.Nodes;
using IntegrationFramework.Core.Engine;
using IntegrationFramework.Worker;
using Xunit;

namespace IntegrationFramework.Tests;

public class SchedulerPolicyTests
{
    [Fact]
    public void Extracts_schedule_trigger_config()
    {
        var graph = new JsonObject
        {
            ["nodes"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "trigger",
                    ["type"] = "trigger",
                    ["config"] = new JsonObject { ["trigger"] = "schedule", ["intervalSeconds"] = 300 }
                })
        }.ToJsonString();

        var schedule = SchedulerPolicy.ExtractSchedule(graph);
        Assert.NotNull(schedule);
        Assert.Equal(300, schedule!.IntervalSeconds);
    }

    [Fact]
    public void Non_schedule_triggers_are_ignored()
    {
        var graph = new JsonObject
        {
            ["nodes"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "trigger",
                    ["type"] = "trigger",
                    ["config"] = new JsonObject { ["trigger"] = "webhook" }
                })
        }.ToJsonString();
        Assert.Null(SchedulerPolicy.ExtractSchedule(graph));
    }

    [Fact]
    public void Zero_or_missing_interval_is_ignored()
    {
        var graph = new JsonObject
        {
            ["nodes"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "trigger",
                    ["type"] = "trigger",
                    ["config"] = new JsonObject { ["trigger"] = "schedule" }
                })
        }.ToJsonString();
        Assert.Null(SchedulerPolicy.ExtractSchedule(graph));
    }

    [Fact]
    public void IsDue_true_when_never_run()
    {
        Assert.True(SchedulerPolicy.IsDue(null, 300, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsDue_false_inside_interval()
    {
        var last = DateTimeOffset.UtcNow.AddSeconds(-100);
        Assert.False(SchedulerPolicy.IsDue(last, 300, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsDue_true_after_interval_elapsed()
    {
        var last = DateTimeOffset.UtcNow.AddSeconds(-301);
        Assert.True(SchedulerPolicy.IsDue(last, 300, DateTimeOffset.UtcNow));
    }
}
