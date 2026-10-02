using System.Text.Json.Nodes;
using IntegrationFramework.Connectors;
using IntegrationFramework.Core.Entities;

namespace IntegrationFramework.Api;

public record WorkflowDto(
    Guid Id, string Name, string? Description, bool Enabled,
    JsonNode? Graph, string? OwnerEmail, string? MyPermission, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static WorkflowDto From(Workflow w, string? myPermission = null) => new(
        w.Id, w.Name, w.Description, w.Enabled,
        JsonNode.Parse(w.GraphJson), w.OwnerEmail, myPermission, w.CreatedAt, w.UpdatedAt);
}

public record UserDto(Guid Id, string Email, string? DisplayName, string Role, DateTimeOffset CreatedAt)
{
    public static UserDto From(AppUser u) => new(u.Id, u.Email, u.DisplayName, u.Role, u.CreatedAt);
}

public record WorkflowShareDto(Guid Id, string Email, string Permission, DateTimeOffset CreatedAt)
{
    public static WorkflowShareDto From(WorkflowShare s) => new(s.Id, s.Email, s.Permission, s.CreatedAt);
}

public record DashboardBucketDto(DateTimeOffset Start, DateTimeOffset End, int Total, int Success, int Failed);

public record DashboardWorkflowDto(Guid WorkflowId, string Name, int Total, int Success, int Failed);

public record DashboardSummaryDto(
    string Range,
    DateTimeOffset From,
    DateTimeOffset To,
    int WorkflowCount,
    int TotalRuns,
    int SuccessRuns,
    int FailedRuns,
    int RunningRuns,
    double SuccessRate,
    double FailureRate,
    IReadOnlyList<DashboardBucketDto> Buckets,
    IReadOnlyList<DashboardWorkflowDto> TopWorkflows);

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

public record EntityMappingDto(
    Guid Id, string Name, string? SourceSystem, string? TargetSystem,
    JsonNode? Mapping, JsonNode? ValidationRules, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static EntityMappingDto From(EntityMapping m) => new(
        m.Id, m.Name, m.SourceSystem, m.TargetSystem,
        ParseOrNull(m.MappingJson), ParseOrNull(m.ValidationRulesJson), m.CreatedAt, m.UpdatedAt);
    private static JsonNode? ParseOrNull(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : TryParse(json);
    private static JsonNode? TryParse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch { return json; }
    }
}

public record EntityMappingValidateResultDto(bool Valid, IReadOnlyList<string> Errors, JsonNode? Mapped);

public record WebhookEventDto(
    Guid Id, Guid WorkflowId, string Status, Guid? RunId, string? Error,
    JsonNode? Body, JsonNode? Headers, DateTimeOffset ReceivedAt)
{
    public static WebhookEventDto From(WebhookEvent e) => new(
        e.Id, e.WorkflowId, e.Status, e.RunId, e.Error,
        ParseOrNull(e.BodyJson), ParseOrNull(e.HeadersJson), e.ReceivedAt);
    private static JsonNode? ParseOrNull(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : TryParse(json);
    private static JsonNode? TryParse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch { return json; }
    }
}
