namespace IntegrationFramework.Core.Entities;

public class Connection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "http"; // http | db
    public string? BaseUrl { get; set; }
    public string AuthType { get; set; } = "none"; // none | api_key | bearer | basic | oauth2
    public string? AuthConfigJson { get; set; }
    public string? DbType { get; set; } // mssql | postgres | mysql (kind == db)
    public string? DbConfigJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
