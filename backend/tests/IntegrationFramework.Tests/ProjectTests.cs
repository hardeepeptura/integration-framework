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
/// Verifies project grouping: CRUD with unique names, the non-empty delete guard,
/// workflow assignment/reassignment/unassignment (empty guid), the workflow list
/// project filter, and dashboard scoping. Access rules are unchanged by design —
/// these tests run anonymous (SSO-off path), where visibility is unrestricted.
/// </summary>
public class ProjectTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _root = new();
    private readonly WebApplicationFactory<Program> _derived;
    private readonly HttpClient _client;

    private static readonly Guid Empty = Guid.Empty;

    public ProjectTests()
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

    private static JsonObject TriggerGraph() => new()
    {
        ["nodes"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "trigger",
                ["type"] = "trigger",
                ["config"] = new JsonObject { ["trigger"] = "manual" }
            })
    };

    private async Task<Guid> CreateWorkflowAsync(string name, Guid? projectId)
    {
        var response = await _client.PostAsJsonAsync("/api/workflows", new
        {
            name,
            graph = TriggerGraph(),
            projectId
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonObject>();
        return Guid.Parse(created!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Project_crud_roundtrip_with_unique_name_guard()
    {
        var create = await _client.PostAsJsonAsync("/api/projects",
            new { name = "Facilities Integrations", description = "FM workflows" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var project = await create.Content.ReadFromJsonAsync<JsonObject>();
        var id = project!["id"]!.GetValue<string>();
        Assert.Equal(0, project["workflowCount"]!.GetValue<int>());

        // Duplicate name → 409.
        var duplicate = await _client.PostAsJsonAsync("/api/projects",
            new { name = "Facilities Integrations" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // Update description + name (rename allowed, still unique-checked).
        var update = await _client.PutAsJsonAsync($"/api/projects/{id}",
            new { name = "Facilities", description = "Facilities management workflows" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("Facilities", updated!["name"]!.GetValue<string>());

        var list = await _client.GetFromJsonAsync<JsonArray>("/api/projects");
        Assert.Single(list!);

        // Empty project deletes fine.
        var delete = await _client.DeleteAsync($"/api/projects/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<JsonArray>("/api/projects"))!);
    }

    [Fact]
    public async Task Project_with_workflows_cannot_be_deleted()
    {
        var create = await _client.PostAsJsonAsync("/api/projects", new { name = "NonEmpty" });
        var project = await create.Content.ReadFromJsonAsync<JsonObject>();
        var id = Guid.Parse(project!["id"]!.GetValue<string>());

        await CreateWorkflowAsync("wf-in-project", id);

        // Delete blocked while a workflow is assigned.
        var blocked = await _client.DeleteAsync($"/api/projects/{id}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var body = await blocked.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Contains("Move or delete", body!["error"]!.GetValue<string>());

        // Unassigning the workflow (empty guid) unblocks deletion.
        var list = await _client.GetFromJsonAsync<JsonArray>("/api/workflows?projectId=" + id);
        var wfId = list![0]!["id"]!.GetValue<string>();
        var unassign = await _client.PutAsJsonAsync($"/api/workflows/{wfId}", new { projectId = Empty });
        Assert.Equal(HttpStatusCode.OK, unassign.StatusCode);
        var unassigned = await unassign.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Null(unassigned!["projectId"]);

        var delete = await _client.DeleteAsync($"/api/projects/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Workflow_assignment_reassignment_and_list_filter()
    {
        var projectA = await _client.PostAsJsonAsync("/api/projects", new { name = "Project A" });
        var a = (await projectA.Content.ReadFromJsonAsync<JsonObject>())!;
        var projectAId = Guid.Parse(a["id"]!.GetValue<string>());
        var projectB = await _client.PostAsJsonAsync("/api/projects", new { name = "Project B" });
        var b = (await projectB.Content.ReadFromJsonAsync<JsonObject>())!;
        var projectBId = Guid.Parse(b["id"]!.GetValue<string>());

        // Create inside project A; create unassigned.
        var wfA = await CreateWorkflowAsync("wf-a", projectAId);
        var wfNone = await CreateWorkflowAsync("wf-none", null);
        await CreateWorkflowAsync("wf-b", projectBId);

        // Assigned at create.
        var detail = await _client.GetFromJsonAsync<JsonObject>($"/api/workflows/{wfA}");
        Assert.Equal(projectAId, Guid.Parse(detail!["projectId"]!.GetValue<string>()));

        // Unknown project → 400.
        var bad = await _client.PostAsJsonAsync("/api/workflows", new
        {
            name = "wf-bad", graph = TriggerGraph(), projectId = Guid.NewGuid()
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Filter: project A only.
        var inA = await _client.GetFromJsonAsync<JsonArray>($"/api/workflows?projectId={projectAId}");
        Assert.Single(inA!);
        Assert.Equal("wf-a", inA[0]!["name"]!.GetValue<string>());

        // Filter: unassigned only (empty guid). The three seeded samples are unassigned too.
        var unassignedList = await _client.GetFromJsonAsync<JsonArray>($"/api/workflows?projectId={Empty}");
        Assert.Equal(4, unassignedList!.Count); // 3 seeded samples + wf-none
        Assert.Contains(unassignedList!, w => w!["name"]!.GetValue<string>() == "wf-none");

        // All workflows ignore the filter.
        var all = await _client.GetFromJsonAsync<JsonArray>("/api/workflows");
        Assert.Equal(6, all!.Count); // 3 seeded samples + wf-a + wf-b + wf-none

        // Reassign wf-a → project B.
        var move = await _client.PutAsJsonAsync($"/api/workflows/{wfA}", new { projectId = projectBId });
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        var inB = await _client.GetFromJsonAsync<JsonArray>($"/api/workflows?projectId={projectBId}");
        Assert.Equal(2, inB!.Count); // wf-a + wf-b
        Assert.Empty((await _client.GetFromJsonAsync<JsonArray>($"/api/workflows?projectId={projectAId}"))!);

        // Project workflow counts reflect the moves.
        var projects = await _client.GetFromJsonAsync<JsonArray>("/api/projects");
        Assert.Equal(0, projects!.First(p => p!["name"]!.GetValue<string>() == "Project A")!["workflowCount"]!.GetValue<int>());
        Assert.Equal(2, projects!.First(p => p!["name"]!.GetValue<string>() == "Project B")!["workflowCount"]!.GetValue<int>());

        _ = wfNone; // created unassigned; asserted above via the empty-guid filter
    }

    [Fact]
    public async Task Dashboard_summary_scopes_to_a_project()
    {
        var project = await _client.PostAsJsonAsync("/api/projects", new { name = "Dash Project" });
        var p = (await project.Content.ReadFromJsonAsync<JsonObject>())!;
        var projectId = Guid.Parse(p["id"]!.GetValue<string>());

        var wfIn = await CreateWorkflowAsync("dash-in", projectId);
        var wfOut = await CreateWorkflowAsync("dash-out", null);
        var now = DateTimeOffset.UtcNow;
        using (var scope = _derived.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
            db.WorkflowRuns.AddRange(
                new WorkflowRun { WorkflowId = wfIn, Status = "success", StartedAt = now.AddMinutes(-5) },
                new WorkflowRun { WorkflowId = wfOut, Status = "failed", StartedAt = now.AddMinutes(-5) });
            db.SaveChanges();
        }

        var scoped = await _client.GetFromJsonAsync<JsonObject>(
            $"/api/dashboard/summary?range=24h&projectId={projectId}");
        Assert.Equal(1, scoped!["totalRuns"]!.GetValue<int>());
        Assert.Equal(1, scoped["successRuns"]!.GetValue<int>());
        Assert.Equal(1, scoped["workflowCount"]!.GetValue<int>()); // only dash-in counted

        var unassigned = await _client.GetFromJsonAsync<JsonObject>(
            $"/api/dashboard/summary?range=24h&projectId={Empty}");
        Assert.Equal(1, unassigned!["totalRuns"]!.GetValue<int>());
        Assert.Equal(1, unassigned["failedRuns"]!.GetValue<int>());

        // No filter → everything visible.
        var all = await _client.GetFromJsonAsync<JsonObject>("/api/dashboard/summary?range=24h");
        Assert.Equal(2, all!["totalRuns"]!.GetValue<int>());
    }
}
