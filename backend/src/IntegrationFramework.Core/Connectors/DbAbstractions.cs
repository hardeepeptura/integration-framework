using System.Text.Json.Nodes;

namespace IntegrationFramework.Core.Connectors;

/// <summary>Resolved, connection-level DB configuration (never contains a raw password).</summary>
public class DbConnectionConfig
{
    public required string DbType { get; init; } // mssql | postgres | mysql
    public required string Host { get; init; }
    public int Port { get; init; }
    public required string Database { get; init; }
    public required string User { get; init; }
    /// <summary>Name of the environment variable that holds the password.</summary>
    public required string PasswordEnv { get; init; }
    public string? SslMode { get; init; }

    public string ResolvePassword() =>
        Environment.GetEnvironmentVariable(PasswordEnv)
        ?? throw new InvalidOperationException(
            $"Environment variable '{PasswordEnv}' is not set; cannot resolve database password.");
}

/// <summary>A fully prepared DB command.</summary>
public class DbRequest
{
    public required string ConnectionString { get; init; }
    public required string Sql { get; init; }
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>();
    public int CommandTimeoutSeconds { get; init; } = 30;
    public bool IsQuery { get; init; } = true;
}

/// <summary>Uniform result shape for query and execute.</summary>
public class DbResult
{
    public JsonArray Rows { get; init; } = [];
    public int RowsAffected { get; init; }
}

/// <summary>Uniform async DB client interface. One implementation per database engine.</summary>
public interface IAsyncDbClient
{
    string DbType { get; }

    Task<DbResult> ExecuteAsync(DbRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Resolves the correct client implementation for a db_type.</summary>
public interface IDbClientFactory
{
    IAsyncDbClient Get(string dbType);
}

/// <summary>Parses/validates connection config JSON and builds connection strings per engine.</summary>
public static class DbConfigHelper
{
    public static DbConnectionConfig Parse(string? dbConfigJson)
    {
        if (string.IsNullOrWhiteSpace(dbConfigJson))
            throw new InvalidOperationException("Connection has no db_config.");

        var obj = JsonNode.Parse(dbConfigJson) as JsonObject
                  ?? throw new InvalidOperationException("db_config must be a JSON object.");

        string Required(string name) =>
            obj.TryGetPropertyValue(name, out var v) && v is JsonValue val && val.TryGetValue<string>(out var s)
                ? s
                : throw new InvalidOperationException($"db_config is missing required field '{name}'.");

        return new DbConnectionConfig
        {
            DbType = Required("db_type"),
            Host = Required("host"),
            Port = obj.TryGetPropertyValue("port", out var p) && p is JsonValue pv && pv.TryGetValue<int>(out var port)
                ? port
                : DefaultPort(Required("db_type")),
            Database = Required("database"),
            User = Required("user"),
            PasswordEnv = Required("password_env"),
            SslMode = obj.TryGetPropertyValue("ssl_mode", out var ssl) && ssl is JsonValue sslVal && sslVal.TryGetValue<string>(out var sslS)
                ? sslS
                : null
        };
    }

    public static int DefaultPort(string dbType) => dbType switch
    {
        "mssql" => 1433,
        "postgres" => 5432,
        "mysql" => 3306,
        _ => throw new InvalidOperationException($"Unknown db_type '{dbType}'. Expected mssql, postgres or mysql.")
    };

    public static string BuildConnectionString(DbConnectionConfig cfg)
    {
        var password = cfg.ResolvePassword();
        return cfg.DbType switch
        {
            "mssql" => $"Server={cfg.Host},{cfg.Port};Database={cfg.Database};User Id={cfg.User};Password={password};TrustServerCertificate=True",
            "postgres" => $"Host={cfg.Host};Port={cfg.Port};Database={cfg.Database};Username={cfg.User};Password={password}" +
                          (cfg.SslMode is null ? string.Empty : $";SSL Mode={cfg.SslMode}"),
            "mysql" => $"Server={cfg.Host};Port={cfg.Port};Database={cfg.Database};User ID={cfg.User};Password={password}" +
                       (cfg.SslMode is null ? string.Empty : $";SSL Mode={cfg.SslMode}"),
            _ => throw new InvalidOperationException($"Unknown db_type '{cfg.DbType}'.")
        };
    }
}

/// <summary>Reads secret values from environment variables. Values never enter logs or DB.</summary>
public static class PasswordResolver
{
    public static string Resolve(string envVarName) =>
        Environment.GetEnvironmentVariable(envVarName)
        ?? throw new InvalidOperationException(
            $"Environment variable '{envVarName}' is not set; cannot resolve secret. " +
            "Configure the secret via environment variables or a Kubernetes Secret.");
}
