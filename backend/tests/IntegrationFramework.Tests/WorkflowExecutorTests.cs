using System.Text.Json.Nodes;
using IntegrationFramework.Connectors.Db;
using IntegrationFramework.Core.Connectors;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Engine;
using IntegrationFramework.Core.Engine.Nodes;
using IntegrationFramework.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntegrationFramework.Tests;

public class WorkflowExecutorTests
{
    private readonly ServiceProvider _provider;
    private readonly MetadataDbContext _db;

    public WorkflowExecutorTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetadataDbContext>(o => o.UseInMemoryDatabase($"executor-{Guid.NewGuid()}"));
        services.AddHttpClient("workflow-http");
        services.AddSingleton<IDbClientFactory, DbClientFactory>();
        services.AddScoped<IWorkflowNode, TriggerNode>();
        services.AddScoped<IWorkflowNode, TransformNode>();
        services.AddScoped<IWorkflowNode, ConditionNode>();
        services.AddScoped<IWorkflowNode, LoopNode>();
        services.AddScoped<IWorkflowNode, DelayNode>();
        services.AddScoped<NodeRegistry>();
        services.AddScoped<WorkflowExecutor>();
        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<MetadataDbContext>();
    }

    private WorkflowExecutor Executor => _provider.GetRequiredService<WorkflowExecutor>();

    private async Task<Workflow> SaveWorkflowAsync(string name, string graphJson)
    {
        var workflow = new Workflow { Name = name, GraphJson = graphJson };
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();
        return workflow;
    }

    private static JsonObject Node(string id, string type, JsonObject? config = null) => new()
    {
        ["id"] = id,
        ["type"] = type,
        ["config"] = config ?? new JsonObject()
    };

    private static string Graph(params JsonObject[] nodes) =>
        new JsonObject { ["nodes"] = new JsonArray(nodes.Select(n => (JsonNode)n).ToArray()) }.ToJsonString();

    [Fact]
    public async Task Transform_run_succeeds_with_mapped_output()
    {
        var graph = Graph(
            Node("trigger", "trigger", new JsonObject { ["trigger"] = "manual" }),
            Node("t1", "transform", new JsonObject
            {
                ["mapping"] = new JsonObject
                {
                    ["sku"] = "$.input.sku",
                    ["qty"] = "$.input.quantity",
                    ["label"] = "SKU {$.input.sku}!",
                    ["static"] = "x"
                }
            }));
        var workflow = await SaveWorkflowAsync("transform-flow", graph);

        var run = await Executor.ExecuteAsync(workflow,
            JsonNode.Parse("""{"sku":"WIDGET-1","quantity":3}"""));

        Assert.Equal("success", run.Status);
        var output = JsonNode.Parse(run.OutputJson!)!;
        Assert.Equal("WIDGET-1", output["sku"]!.GetValue<string>());
        Assert.Equal(3, output["qty"]!.GetValue<int>());
        Assert.Equal("SKU WIDGET-1!", output["label"]!.GetValue<string>());
        Assert.Equal("x", output["static"]!.GetValue<string>());

        var steps = run.StepRuns.OrderBy(s => s.StartedAt).ToList();
        Assert.Equal(2, steps.Count);
        Assert.All(steps, s => Assert.Equal("success", s.Status));
        Assert.All(steps, s => Assert.True(s.DurationMs >= 0));
    }

    [Fact]
    public async Task Condition_routes_to_correct_branch()
    {
        var graph = Graph(
            Node("trigger", "trigger", new JsonObject { ["trigger"] = "manual" }),
            Node("cond", "condition", new JsonObject
            {
                ["left"] = "$.input.score",
                ["operator"] = "gt",
                ["right"] = 50,
                ["on_true"] = new JsonArray("hi"),
                ["on_false"] = new JsonArray("lo")
            }),
            Node("hi", "transform", new JsonObject { ["mapping"] = new JsonObject { ["tier"] = "high" } }),
            Node("lo", "transform", new JsonObject { ["mapping"] = new JsonObject { ["tier"] = "low" } }));

        var workflow = await SaveWorkflowAsync("condition-flow", graph);

        var highRun = await Executor.ExecuteAsync(workflow, JsonNode.Parse("""{"score":90}"""));
        Assert.Equal("success", highRun.Status);
        Assert.Equal("high", JsonNode.Parse(highRun.OutputJson!)!["tier"]!.GetValue<string>());
        // The last node executed in the high branch is "hi".
        Assert.Contains(highRun.StepRuns, s => s.NodeId == "hi" && s.Status == "success");
        Assert.DoesNotContain(highRun.StepRuns, s => s.NodeId == "lo" && s.Status == "success");

        var lowRun = await Executor.ExecuteAsync(workflow, JsonNode.Parse("""{"score":10}"""));
        Assert.Equal("low", JsonNode.Parse(lowRun.OutputJson!)!["tier"]!.GetValue<string>());
        Assert.DoesNotContain(lowRun.StepRuns, s => s.NodeId == "hi" && s.Status == "success");
    }

    [Fact]
    public async Task Loop_executes_body_per_item_and_exposes_value()
    {
        var graph = Graph(
            Node("trigger", "trigger", new JsonObject { ["trigger"] = "manual" }),
            Node("loop-1", "loop", new JsonObject
            {
                ["source"] = "$.input.items",
                ["body"] = new JsonArray("pick")
            }),
            Node("pick", "transform", new JsonObject
            {
                ["mapping"] = new JsonObject { ["sku"] = "$.steps.loop-1.value.sku" }
            }));
        var workflow = await SaveWorkflowAsync("loop-flow", graph);

        var run = await Executor.ExecuteAsync(workflow,
            JsonNode.Parse("""{"items":[{"sku":"A"},{"sku":"B"},{"sku":"C"}]}"""));

        Assert.Equal("success", run.Status);
        var picks = run.StepRuns.Where(s => s.NodeId == "pick").ToList();
        Assert.Equal(3, picks.Count); // one StepRun per iteration
        var loopOutput = JsonNode.Parse(run.StepRuns.First(s => s.NodeId == "loop-1").OutputJson!)!;
        Assert.Equal(3, loopOutput["count"]!.GetValue<int>());
        Assert.Equal("C", loopOutput["value"]!["sku"]!.GetValue<string>());
    }

    [Fact]
    public async Task Failed_node_without_on_error_continue_fails_run()
    {
        var graph = Graph(
            Node("trigger", "trigger", new JsonObject { ["trigger"] = "manual" }),
            Node("boom", "transform", new JsonObject())); // missing 'mapping'
        var workflow = await SaveWorkflowAsync("boom-flow", graph);

        var run = await Executor.ExecuteAsync(workflow, JsonNode.Parse("{}"));

        Assert.Equal("failed", run.Status);
        Assert.Contains("boom", run.Error);
        var boomStep = run.StepRuns.Single(s => s.NodeId == "boom");
        Assert.Equal("failed", boomStep.Status);
        Assert.NotNull(boomStep.Error);
    }

    [Fact]
    public async Task Failed_node_with_on_error_continue_lets_run_succeed()
    {
        var graph = Graph(
            Node("trigger", "trigger", new JsonObject { ["trigger"] = "manual" }),
            Node("boom", "transform", new JsonObject { ["onError"] = "continue" }),
            Node("after", "transform", new JsonObject
            {
                ["mapping"] = new JsonObject { ["reached"] = true }
            }));
        var workflow = await SaveWorkflowAsync("continue-flow", graph);

        var run = await Executor.ExecuteAsync(workflow, JsonNode.Parse("{}"));

        Assert.Equal("success", run.Status);
        Assert.Contains(run.StepRuns, s => s.NodeId == "boom" && s.Status == "failed");
        Assert.Contains(run.StepRuns, s => s.NodeId == "after" && s.Status == "success");
    }

    [Fact]
    public async Task Retry_config_attempts_node_multiple_times()
    {
        // transform without mapping always fails; attempts recorded even on failure.
        var graph = Graph(
            Node("trigger", "trigger", new JsonObject { ["trigger"] = "manual" }),
            Node("flaky", "transform", new JsonObject
            {
                ["retry"] = new JsonObject { ["attempts"] = 3 },
                ["onError"] = "continue"
            }));
        var workflow = await SaveWorkflowAsync("retry-flow", graph);

        var run = await Executor.ExecuteAsync(workflow, JsonNode.Parse("{}"));

        var step = run.StepRuns.Single(s => s.NodeId == "flaky");
        Assert.Equal("failed", step.Status);
        Assert.Equal(3, step.Attempts);
    }

    [Fact]
    public async Task Delay_zero_seconds_completes()
    {
        var graph = Graph(
            Node("trigger", "trigger", new JsonObject { ["trigger"] = "manual" }),
            Node("wait", "delay", new JsonObject { ["seconds"] = 0 }));
        var workflow = await SaveWorkflowAsync("delay-flow", graph);

        var run = await Executor.ExecuteAsync(workflow, null);

        Assert.Equal("success", run.Status);
        Assert.Equal(0, JsonNode.Parse(run.StepRuns.First(s => s.NodeId == "wait").OutputJson!)!["delayedSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task Run_input_and_output_persisted()
    {
        var graph = Graph(Node("trigger", "trigger", new JsonObject { ["trigger"] = "manual" }));
        var workflow = await SaveWorkflowAsync("persist-flow", graph);

        var run = await Executor.ExecuteAsync(workflow, JsonNode.Parse("""{"hello":"world"}"""));

        var stored = await _db.WorkflowRuns.Include(r => r.StepRuns).SingleAsync(r => r.Id == run.Id);
        Assert.Equal("success", stored.Status);
        Assert.NotNull(stored.InputJson);
        Assert.Contains("world", stored.InputJson);
        Assert.NotNull(stored.FinishedAt);
        Assert.Single(stored.StepRuns);
    }
}
