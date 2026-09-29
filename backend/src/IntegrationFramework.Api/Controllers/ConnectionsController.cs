using System.Text.Json.Nodes;
using IntegrationFramework.Connectors;
using IntegrationFramework.Core.Connectors;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Api.Controllers;

[ApiController]
[Route("api/connections")]
public class ConnectionsController(
    MetadataDbContext db,
    IDbClientFactory dbClientFactory,
    IHttpClientFactory httpClientFactory,
    OAuth2TokenManager oauth2Tokens) : ControllerBase
{
    public class ConnectionRequest
    {
        public string? Name { get; set; }
        public string? Kind { get; set; } // http | db
        public string? BaseUrl { get; set; }
        public string? AuthType { get; set; }
        public JsonNode? AuthConfig { get; set; }
        public string? DbType { get; set; }
        public JsonNode? DbConfig { get; set; }
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ConnectionDto>>> List() =>
        Ok((await db.Connections.OrderBy(c => c.Name).ToListAsync()).Select(ConnectionDto.From));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ConnectionDto>> Get(Guid id)
    {
        var connection = await db.Connections.FindAsync([id]);
        return connection is null ? NotFound() : Ok(ConnectionDto.From(connection));
    }

    [HttpPost]
    public async Task<ActionResult<ConnectionDto>> Create([FromBody] ConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });
        var kind = request.Kind ?? "http";
        if (kind is not ("http" or "db"))
            return BadRequest(new { error = "Kind must be 'http' or 'db'." });
        var dbConfig = UnwrapJson(request.DbConfig);
        var authConfig = UnwrapJson(request.AuthConfig);
        if (kind == "db")
        {
            var probe = ParseDbConfig(request.DbType, dbConfig);
            if (probe is not null) return BadRequest(new { error = probe });
        }

        var connection = new Connection
        {
            Name = request.Name,
            Kind = kind,
            BaseUrl = request.BaseUrl,
            AuthType = kind == "http" ? (request.AuthType ?? "none") : "none",
            AuthConfigJson = authConfig?.ToJsonString(),
            DbType = kind == "db" ? request.DbType : null,
            DbConfigJson = dbConfig?.ToJsonString()
        };
        db.Connections.Add(connection);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = connection.Id }, ConnectionDto.From(connection));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ConnectionDto>> Update(Guid id, [FromBody] ConnectionRequest request)
    {
        var connection = await db.Connections.FindAsync([id]);
        if (connection is null) return NotFound();

        if (request.Name is not null) connection.Name = request.Name;
        if (request.BaseUrl is not null) connection.BaseUrl = request.BaseUrl;
        if (request.AuthType is not null && connection.Kind == "http") connection.AuthType = request.AuthType;
        if (UnwrapJson(request.AuthConfig) is not null) connection.AuthConfigJson = UnwrapJson(request.AuthConfig)!.ToJsonString();
        if (request.DbType is not null && connection.Kind == "db") connection.DbType = request.DbType;
        if (UnwrapJson(request.DbConfig) is not null) connection.DbConfigJson = UnwrapJson(request.DbConfig)!.ToJsonString();
        await db.SaveChangesAsync();
        return Ok(ConnectionDto.From(connection));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var connection = await db.Connections.FindAsync([id]);
        if (connection is null) return NotFound();
        db.Connections.Remove(connection);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Validates HTTP reachability/auth or DB connectivity for the connection.</summary>
    [HttpPost("{id:guid}/test")]
    public async Task<ActionResult<ConnectionTestResultDto>> Test(Guid id, CancellationToken ct)
    {
        var connection = await db.Connections.FindAsync([id]);
        if (connection is null) return NotFound();

        try
        {
            if (connection.Kind == "db")
            {
                var probe = ParseDbConfig(connection.DbType, connection.DbConfigJson);
                if (probe is not null) return Ok(new ConnectionTestResultDto(false, probe));

                var config = DbConfigHelper.Parse(connection.DbConfigJson);
                var client = dbClientFactory.Get(config.DbType);
                await client.ExecuteAsync(new DbRequest
                {
                    ConnectionString = DbConfigHelper.BuildConnectionString(config),
                    Sql = "SELECT 1",
                    CommandTimeoutSeconds = 10,
                    IsQuery = true
                }, ct);
                return Ok(new ConnectionTestResultDto(true, $"Connected to {config.DbType} at {config.Host}."));
            }

            // OAuth2: prove the client can obtain an access token before probing the base URL.
            if (connection.AuthType == "oauth2")
            {
                var token = await oauth2Tokens.GetAccessTokenAsync(connection, ct);
                if (string.IsNullOrWhiteSpace(connection.BaseUrl))
                    return Ok(new ConnectionTestResultDto(true, "OAuth2 token acquired."));

                using var oauthProbe = new HttpRequestMessage(HttpMethod.Head, connection.BaseUrl);
                oauthProbe.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var oauthClient = httpClientFactory.CreateClient("workflow-http");
                oauthClient.Timeout = TimeSpan.FromSeconds(10);
                using var oauthResponse = await oauthClient.SendAsync(oauthProbe, ct);
                var oauthOk = (int)oauthResponse.StatusCode < 500;
                return Ok(new ConnectionTestResultDto(oauthOk,
                    $"OAuth2 token acquired; HTTP {(int)oauthResponse.StatusCode} {oauthResponse.ReasonPhrase} from {connection.BaseUrl}"));
            }

            // HTTP connection: apply auth and issue a HEAD (fallback GET) against the base URL.
            if (string.IsNullOrWhiteSpace(connection.BaseUrl))
                return Ok(new ConnectionTestResultDto(false, "HTTP connection has no base_url."));

            using var request = new HttpRequestMessage(HttpMethod.Head, connection.BaseUrl);
            HttpAuthApplier.Apply(request, connection.AuthType, connection.AuthConfigJson);
            using var httpClient = httpClientFactory.CreateClient("workflow-http");
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            using var response = await httpClient.SendAsync(request, ct);
            var ok = (int)response.StatusCode < 500; // 404/405 on HEAD is still "reachable"
            return Ok(new ConnectionTestResultDto(ok,
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} from {connection.BaseUrl}"));
        }
        catch (Exception ex)
        {
            return Ok(new ConnectionTestResultDto(false, ex.Message));
        }
    }

    /// <summary>Accepts config as a JSON object or as a JSON-encoded object string ('{"host": ...}').</summary>
    private static JsonNode? UnwrapJson(JsonNode? node) =>
        node is JsonValue val && val.TryGetValue<string>(out var s)
            ? TryParseJson(s) ?? node
            : node;

    private static JsonNode? TryParseJson(string json)
    {
        try { return JsonNode.Parse(json); }
        catch { return null; }
    }

    private static string? ParseDbConfig(string? dbType, JsonNode? dbConfig)
    {
        if (dbType is not ("mssql" or "postgres" or "mysql"))
            return "DbType must be 'mssql', 'postgres' or 'mysql'.";
        if (dbConfig is null)
            return "DbConfig is required for db connections.";
        try
        {
            DbConfigHelper.Parse(dbConfig.ToJsonString());
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }
}
