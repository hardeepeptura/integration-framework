using System.Text.Json.Nodes;
using IntegrationFramework.Connectors.Db;
using IntegrationFramework.Core.Connectors;
using IntegrationFramework.Core.Engine;
using IntegrationFramework.Core.Engine.Nodes;
using Xunit;

namespace IntegrationFramework.Tests;

public class DbConfigHelperTests : IDisposable
{
    private const string PasswordVar = "IF_TEST_DB_PASSWORD";

    public void Dispose() => Environment.SetEnvironmentVariable(PasswordVar, null);

    private static string ConfigJson(string dbType, int? port = null) => new JsonObject
    {
        ["db_type"] = dbType,
        ["host"] = "db.example.com",
        ["port"] = port,
        ["database"] = "testdb",
        ["user"] = "svc-user",
        ["password_env"] = PasswordVar
    }.ToJsonString();

    [Fact]
    public void Parses_config_with_defaults()
    {
        Environment.SetEnvironmentVariable(PasswordVar, "pw");
        var config = DbConfigHelper.Parse(ConfigJson("mssql"));
        Assert.Equal("db.example.com", config.Host);
        Assert.Equal(1433, config.Port);
        Assert.Equal("testdb", config.Database);
        Assert.Equal("svc-user", config.User);
        Assert.Equal(PasswordVar, config.PasswordEnv);
    }

    [Theory]
    [InlineData("mssql", 1433)]
    [InlineData("postgres", 5432)]
    [InlineData("mysql", 3306)]
    public void Default_ports_are_correct(string dbType, int expectedPort)
    {
        Assert.Equal(expectedPort, DbConfigHelper.DefaultPort(dbType));
    }

    [Fact]
    public void Build_connection_string_mssql()
    {
        Environment.SetEnvironmentVariable(PasswordVar, "pw-mssql");
        var cs = DbConfigHelper.BuildConnectionString(DbConfigHelper.Parse(ConfigJson("mssql")));
        Assert.Contains("Server=db.example.com,1433", cs);
        Assert.Contains("Database=testdb", cs);
        Assert.Contains("User Id=svc-user", cs);
        Assert.Contains("Password=pw-mssql", cs);
    }

    [Fact]
    public void Build_connection_string_postgres_with_ssl()
    {
        Environment.SetEnvironmentVariable(PasswordVar, "pw-pg");
        var json = new JsonObject
        {
            ["db_type"] = "postgres",
            ["host"] = "pg.example.com",
            ["database"] = "pgdb",
            ["user"] = "pguser",
            ["password_env"] = PasswordVar,
            ["ssl_mode"] = "Require"
        }.ToJsonString();
        var cs = DbConfigHelper.BuildConnectionString(DbConfigHelper.Parse(json));
        Assert.Contains("Host=pg.example.com", cs);
        Assert.Contains("Username=pguser", cs);
        Assert.Contains("SSL Mode=Require", cs);
        Assert.Contains("Password=pw-pg", cs);
    }

    [Fact]
    public void Build_connection_string_mysql()
    {
        Environment.SetEnvironmentVariable(PasswordVar, "pw-my");
        var cs = DbConfigHelper.BuildConnectionString(DbConfigHelper.Parse(ConfigJson("mysql")));
        Assert.Contains("Server=db.example.com", cs);
        Assert.Contains("Database=testdb", cs);
        Assert.Contains("Password=pw-my", cs);
    }

    [Fact]
    public void Missing_password_env_var_fails_with_clear_error()
    {
        var config = DbConfigHelper.Parse(ConfigJson("mssql"));
        var ex = Assert.Throws<InvalidOperationException>(() => DbConfigHelper.BuildConnectionString(config));
        Assert.Contains(PasswordVar, ex.Message);
    }

    [Fact]
    public void Missing_required_field_fails()
    {
        var json = new JsonObject
        {
            ["db_type"] = "mssql",
            ["host"] = "db.example.com"
        }.ToJsonString();
        var ex = Assert.Throws<InvalidOperationException>(() => DbConfigHelper.Parse(json));
        Assert.Contains("database", ex.Message);
    }

    [Fact]
    public void Unknown_db_type_fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DbConfigHelper.DefaultPort("oracle"));
        Assert.Contains("oracle", ex.Message);
    }
}

public class DbClientFactoryTests
{
    [Fact]
    public void Returns_correct_client_per_type()
    {
        var factory = new DbClientFactory();
        Assert.Equal("mssql", factory.Get("mssql").DbType);
        Assert.Equal("postgres", factory.Get("postgres").DbType);
        Assert.Equal("mysql", factory.Get("mysql").DbType);
        Assert.IsType<SqlServerDbClient>(factory.Get("mssql"));
        Assert.IsType<PostgreSqlDbClient>(factory.Get("postgres"));
        Assert.IsType<MySqlDbClient>(factory.Get("mysql"));
    }

    [Fact]
    public void Unknown_type_throws()
    {
        Assert.Throws<InvalidOperationException>(() => new DbClientFactory().Get("oracle"));
    }
}

public class DbParameterBindingTests
{
    [Fact]
    public void Rewrites_named_parameters_to_positional()
    {
        var (sql, parameters) = DbQueryNode.Bind(
            "SELECT * FROM leads WHERE sku = :sku AND qty >= :qty",
            new Dictionary<string, object?> { ["sku"] = "W1", ["qty"] = 5 });

        Assert.Equal("SELECT * FROM leads WHERE sku = @p0 AND qty >= @p1", sql);
        Assert.Equal("W1", parameters["p0"]);
        Assert.Equal(5, parameters["p1"]);
    }

    [Fact]
    public void Same_parameter_used_twice_binds_twice()
    {
        var (sql, parameters) = DbQueryNode.Bind(
            "SELECT * FROM t WHERE a = :x OR b = :x",
            new Dictionary<string, object?> { ["x"] = 1 });

        Assert.Equal("SELECT * FROM t WHERE a = @p0 OR b = @p1", sql);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void Unsupplied_parameter_fails_with_clear_error()
    {
        var ex = Assert.Throws<NodeExecutionException>(() => DbQueryNode.Bind(
            "SELECT * FROM t WHERE a = :missing",
            new Dictionary<string, object?>()));
        Assert.Contains(":missing", ex.Message);
    }

    [Fact]
    public void Sql_without_parameters_unchanged()
    {
        var (sql, parameters) = DbQueryNode.Bind("SELECT 1", new Dictionary<string, object?>());
        Assert.Equal("SELECT 1", sql);
        Assert.Empty(parameters);
    }
}
