namespace IntegrationFramework.Core.Entities;

/// <summary>
/// A named field mapping between two systems (e.g. "CRM Lead → Inventory Item").
/// MappingJson: { "fields": [ { "source": "$.input.name" | "relative.path", "target": "customer_name",
///   "required": true, "default": "n/a", "transforms": ["trim","upper","lower"] } ] }
/// ValidationRulesJson: { "rules": [ { "field": "email", "type": "string|number|boolean",
///   "required": true, "minLength": 1, "maxLength": 200, "min": 0, "max": 100, "pattern": "^..." } ] }
/// Field "source" starting with "$." resolves against the full run state ($./$.input/$.steps/...);
/// relative paths resolve against the node's configured source payload.
/// </summary>
public class EntityMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? SourceSystem { get; set; }
    public string? TargetSystem { get; set; }
    public string MappingJson { get; set; } = "{}";
    public string? ValidationRulesJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
