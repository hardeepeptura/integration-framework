using System.Text.Json.Nodes;
using IntegrationFramework.Connectors;
using IntegrationFramework.Core.Entities;

namespace IntegrationFramework.Api;

public record WorkflowDto(
    Guid Id, string Name, string? Description, bool Enabled,
    JsonNode? Graph, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static WorkflowDto From(Workflow w) => new(
        w.Id, w.Name, w.Description, w.Enabled,
        JsonNode.Parse(w.GraphJson), w.CreatedAt, w.UpdatedAt);
}

public record ConnectionDto(
    Guid Id, string Name, string Kind, string? BaseUrl, string AuthType, string? AuthConfig,
    string? DbType, string? DbConfig, DateTimeOffset CreatedAt)
{
    public static ConnectionDto From(Connection c) => new(
        c.Id, c.Name, c.Kind, c.BaseUrl, c.AuthType,
        ConnectionMasker.MaskConfigJson(c.AuthConfigJson),
        c.DbType,
        ConnectionMasker.MaskConfigJson(c.DbConfigJson),
        c.CreatedAt);
}

public record RunStepDto(
    Guid Id, string NodeId, string NodeType, string Status, JsonNode? Input, JsonNode? Output,
    string? Error, int Attempts, long DurationMs, DateTimeOffset StartedAt);

public record RunDto(
    Guid Id, Guid WorkflowId, string Status, JsonNode? Input, JsonNode? Output, string? Error,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, IReadOnlyList<RunStepDto> Steps)
{
    public static RunDto From(WorkflowRun r, bool includeSteps = true) => new(
        r.Id, r.WorkflowId, r.Status,
        ParseOrNull(r.InputJson), ParseOrNull(r.OutputJson), r.Error, r.StartedAt, r.FinishedAt,
        includeSteps
            ? r.StepRuns.OrderBy(s => s.StartedAt).ThenBy(s => s.Id)
                .Select(s => new RunStepDto(s.Id, s.NodeId, s.NodeType, s.Status, ParseOrNull(s.InputJson),
                    ParseOrNull(s.OutputJson), s.Error, s.Attempts, s.DurationMs, s.StartedAt)).ToList()
            : []);
    private static JsonNode? ParseOrNull(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : TryParse(json);
    private static JsonNode? TryParse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch { return json; }
    }
}

public record ValidationResultDto(bool Valid, IReadOnlyList<string> Errors);

public record ConnectionTestResultDto(bool Success, string Detail);
