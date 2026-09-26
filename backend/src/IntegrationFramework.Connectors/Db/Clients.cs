using System.Text.Json.Nodes;
using IntegrationFramework.Core.Connectors;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace IntegrationFramework.Connectors.Db;

/// <summary>Shared ADO.NET result-mapping logic.</summary>
internal static class DbResultMapper
{
    public static DbResult FromReader(System.Data.Common.DbDataReader reader) =>
        new() { Rows = MapRows(reader) };

    public static JsonArray MapRows(System.Data.Common.DbDataReader reader)
    {
        var rows = new JsonArray();
        while (reader.Read())
        {
            var row = new JsonObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                row[name] = reader.IsDBNull(i) ? null : ToJson(reader.GetFieldType(i), reader.GetValue(i));
            }
            rows.Add(row);
        }
        return rows;
    }

    private static JsonNode? ToJson(Type type, object value) => value switch
    {
        DBNull or null => null,
        bool b => b,
        byte or sbyte or short or ushort or int or uint or long or ulong => JsonValue.Create(Convert.ToInt64(value)),
        float or double or decimal => JsonValue.Create(Convert.ToDecimal(value)),
        string s => s,
        DateTime dt => (JsonNode)dt,
        DateTimeOffset dto => (JsonNode)dto,
        DateOnly d => d.ToString("yyyy-MM-dd"),
        Guid g => g.ToString(),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => value.ToString()
    };
}

public class SqlServerDbClient : IAsyncDbClient
{
    public string DbType => "mssql";

    public async Task<DbResult> ExecuteAsync(DbRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(request.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = request.Sql;
        command.CommandTimeout = request.CommandTimeoutSeconds;
        foreach (var (name, value) in request.Parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        if (request.IsQuery)
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return DbResultMapper.FromReader(reader);
        }
        return new DbResult { RowsAffected = await command.ExecuteNonQueryAsync(cancellationToken) };
    }
}

public class PostgreSqlDbClient : IAsyncDbClient
{
    public string DbType => "postgres";

    public async Task<DbResult> ExecuteAsync(DbRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(request.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = request.Sql;
        command.CommandTimeout = request.CommandTimeoutSeconds;
        foreach (var (name, value) in request.Parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        if (request.IsQuery)
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return DbResultMapper.FromReader(reader);
        }
        return new DbResult { RowsAffected = await command.ExecuteNonQueryAsync(cancellationToken) };
    }
}

public class MySqlDbClient : IAsyncDbClient
{
    public string DbType => "mysql";

    public async Task<DbResult> ExecuteAsync(DbRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(request.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = request.Sql;
        command.CommandTimeout = request.CommandTimeoutSeconds;
        foreach (var (name, value) in request.Parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        if (request.IsQuery)
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return DbResultMapper.FromReader(reader);
        }
        return new DbResult { RowsAffected = await command.ExecuteNonQueryAsync(cancellationToken) };
    }
}

public class DbClientFactory : IDbClientFactory
{
    public IAsyncDbClient Get(string dbType) => dbType switch
    {
        "mssql" => new SqlServerDbClient(),
        "postgres" => new PostgreSqlDbClient(),
        "mysql" => new MySqlDbClient(),
        _ => throw new InvalidOperationException($"Unknown db_type '{dbType}'. Expected mssql, postgres or mysql.")
    };
}
