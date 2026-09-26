using System.Text.Json.Nodes;
using IntegrationFramework.Core.Connectors;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntegrationFramework.Tests;

public class WorkflowValidatorTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly WorkflowValidator _validator;

    public WorkflowValidatorTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetadataDbContext>(o => o.UseInMemoryDatabase($"validator-{Guid.NewGuid()}"));
        _provider = services.BuildServiceProvider();
        var db = _provider.GetRequiredService<MetadataDbContext>();
        _validator = new WorkflowValidator(db);
    }

    public void Dispose() => _provider.Dispose();

    private static string Graph(params JsonObject[] nodes) =>
        new JsonObject { ["nodes"] = new JsonArray(nodes.Select(n => (JsonNode)n).ToArray()) }.ToJsonString();

    private static JsonObject Node(string id, string type, JsonObject? config = null) => new()
    {
        ["id"] = id,
        ["type"] = type,
        ["config"] = config ?? new JsonObject()
    };

    [Fact]
    public async Task Valid_transform_flow_passes()
    {
        var graph = Graph(
            Node("trigger", "trigger", new JsonObject { ["trigger"] = "manual" }),
            Node("t1", "transform", new JsonObject { ["mapping"] = new JsonObject { ["a"] = "$.input.x" } }));
        var errors = await _validator.ValidateAsync(graph);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Missing_trigger_fails()
    {
        var graph = Graph(Node("t1", "transform", new JsonObject { ["mapping"] = new JsonObject() }));
        var errors = await _validator.ValidateAsync(graph);
        Assert.Contains(errors, e => e.Contains("trigger"));
    }

    [Fact]
    public async Task Unknown_node_type_fails()
    {
        var graph = Graph(
            Node("trigger", "trigger"),
            Node("x", "quantum_flux"));
        var errors = await _validator.ValidateAsync(graph);
        Assert.Contains(errors, e => e.Contains("quantum_flux"));
    }

    [Fact]
    public async Task Duplicate_node_ids_fail()
    {
        var graph = Graph(
            Node("trigger", "trigger"),
            Node("dup", "transform", new JsonObject { ["mapping"] = new JsonObject() }),
            Node("dup", "delay", new JsonObject { ["seconds"] = 0 }));
        var errors = await _validator.ValidateAsync(graph);
        Assert.Contains(errors, e => e.Contains("Duplicate node id"));
    }

    [Theory]
    [InlineData("http_request", "url")]
    [InlineData("db_query", "connectionId")]
    [InlineData("db_query", "sql")]
    [InlineData("loop", "source")]
    public async Task Missing_required_config_fails(string type, string field)
    {
        var graph = Graph(
            Node("trigger", "trigger"),
            Node("n1", type, new JsonObject()));
        var errors = await _validator.ValidateAsync(graph);
        Assert.Contains(errors, e => e.Contains(field));
    }

    [Fact]
    public async Task Nonexistent_connection_reference_fails()
    {
        var graph = Graph(
            Node("trigger", "trigger"),
            Node("q", "db_query", new JsonObject
            {
                ["connectionId"] = Guid.NewGuid().ToString(),
                ["sql"] = "SELECT 1"
            }));
        var errors = await _validator.ValidateAsync(graph);
        Assert.Contains(errors, e => e.Contains("does not exist"));
    }

    [Fact]
    public async Task Branch_references_to_unknown_nodes_fail()
    {
        var graph = Graph(
            Node("trigger", "trigger"),
            Node("cond", "condition", new JsonObject
            {
                ["left"] = "$.input.x",
                ["operator"] = "eq",
                ["right"] = 1,
                ["on_true"] = new JsonArray("ghost")
            }));
        var errors = await _validator.ValidateAsync(graph);
        Assert.Contains(errors, e => e.Contains("ghost"));
    }

    [Fact]
    public async Task Invalid_operator_fails()
    {
        var graph = Graph(
            Node("trigger", "trigger"),
            Node("cond", "condition", new JsonObject
            {
                ["left"] = "$.input.x",
                ["operator"] = "like",
                ["right"] = 1
            }));
        var errors = await _validator.ValidateAsync(graph);
        Assert.Contains(errors, e => e.Contains("operator"));
    }

    [Fact]
    public async Task Empty_graph_fails()
    {
        var errors = await _validator.ValidateAsync("{\"nodes\":[]}");
        Assert.Single(errors);
    }

    [Fact]
    public async Task Parse_failure_is_reported()
    {
        var errors = await _validator.ValidateAsync("not json at all");
        Assert.Single(errors);
        Assert.Contains("Invalid graph", errors[0]);
    }
}
