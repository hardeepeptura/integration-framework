using System.Text.Json.Nodes;
using IntegrationFramework.Core.Engine;
using Xunit;

namespace IntegrationFramework.Tests;

public class PathResolverTests
{
    private static RunContext CreateContext()
    {
        var input = JsonNode.Parse("""{"sku":"WIDGET-1","quantity":2,"items":[{"id":"i1"},{"id":"i2"}]}""");
        var ctx = new RunContext(input, new Dictionary<string, string> { ["self_base_url"] = "http://localhost:8000" });
        ctx.SetStepOutput("http-1", JsonNode.Parse("""{"status":200,"body":{"id":"abc","count":3}}"""));
        ctx.SetStepOutput("loop-1", JsonNode.Parse("""{"index":1,"value":{"sku":"GIZMO-2"}}"""));
        return ctx;
    }

    [Fact]
    public void Resolves_input_nested_path()
    {
        var ctx = CreateContext();
        Assert.Equal("WIDGET-1", ctx.ResolvePath("$.input.sku")!.GetValue<string>());
        Assert.Equal(2, ctx.ResolvePath("$.input.quantity")!.GetValue<int>());
    }

    [Fact]
    public void Resolves_input_array_index()
    {
        var ctx = CreateContext();
        Assert.Equal("i2", ctx.ResolvePath("$.input.items[1].id")!.GetValue<string>());
        Assert.Equal("i1", ctx.ResolvePath("$.input.items[0].id")!.GetValue<string>());
    }

    [Fact]
    public void Resolves_step_output_paths()
    {
        var ctx = CreateContext();
        Assert.Equal("abc", ctx.ResolvePath("$.steps.http-1.body.id")!.GetValue<string>());
        Assert.Equal(3, ctx.ResolvePath("$.steps.http-1.body.count")!.GetValue<int>());
        Assert.Equal("GIZMO-2", ctx.ResolvePath("$.steps.loop-1.value.sku")!.GetValue<string>());
    }

    [Fact]
    public void Resolves_env_reference()
    {
        var ctx = CreateContext();
        Assert.Equal("http://localhost:8000", ctx.ResolvePath("$.env.self_base_url")!.GetValue<string>());
    }

    [Fact]
    public void Missing_paths_resolve_to_null()
    {
        var ctx = CreateContext();
        Assert.Null(ctx.ResolvePath("$.input.missing"));
        Assert.Null(ctx.ResolvePath("$.input.items[5].id"));
        Assert.Null(ctx.ResolvePath("$.steps.nosuchnode.x"));
        Assert.Null(ctx.ResolvePath("$.env.nosuchvar"));
    }

    [Fact]
    public void Full_reference_preserves_type()
    {
        var ctx = CreateContext();
        var resolved = ctx.ResolveString("$.input.quantity")!;
        Assert.Equal(2, resolved.GetValue<int>());
        var whole = ctx.ResolveString("$.input")!;
        Assert.True(whole is JsonObject);
    }

    [Fact]
    public void Interpolates_inside_strings()
    {
        var ctx = CreateContext();
        var resolved = ctx.ResolveString("Reserve {$.input.quantity} x {$.input.sku} via {$.env.self_base_url}")!;
        Assert.Equal("Reserve 2 x WIDGET-1 via http://localhost:8000", resolved.GetValue<string>());
    }

    [Fact]
    public void Interpolation_with_missing_path_becomes_empty()
    {
        var ctx = CreateContext();
        var resolved = ctx.ResolveString("hello {$.input.nope}")!;
        Assert.Equal("hello ", resolved.GetValue<string>());
    }

    [Fact]
    public void Plain_strings_pass_through()
    {
        var ctx = CreateContext();
        Assert.Equal("just text", ctx.ResolveString("just text")!.GetValue<string>());
        Assert.Equal(42, ctx.Resolve(JsonValue.Create(42))!.GetValue<int>());
    }

    [Fact]
    public void Resolves_nested_objects_recursively()
    {
        var ctx = CreateContext();
        var template = JsonNode.Parse("""{"a":"$.input.sku","b":{"c":"$.steps.http-1.body.id"},"d":5}""");
        var resolved = ctx.Resolve(template) as JsonObject;
        Assert.NotNull(resolved);
        Assert.Equal("WIDGET-1", resolved!["a"]!.GetValue<string>());
        Assert.Equal("abc", resolved["b"]!["c"]!.GetValue<string>());
        Assert.Equal(5, resolved["d"]!.GetValue<int>());
    }
}
