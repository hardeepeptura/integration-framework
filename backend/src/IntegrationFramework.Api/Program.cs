using IntegrationFramework.Api.Demo;
using IntegrationFramework.Connectors.Db;
using IntegrationFramework.Core.Connectors;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Engine;
using IntegrationFramework.Core.Engine.Nodes;
using IntegrationFramework.Worker;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

using System.Xml.Linq;

var builder = WebApplication.CreateBuilder(args);

// ----- Metadata store: SQL Server (or Azure SQL) when configured, InMemory otherwise -----
var metadataConnectionString =
    builder.Configuration.GetConnectionString("Metadata")
    ?? builder.Configuration["METADATA_CONNECTION_STRING"]
    ?? Environment.GetEnvironmentVariable("METADATA_CONNECTION_STRING");

builder.Services.AddDbContext<MetadataDbContext>(options =>
{
    if (!string.IsNullOrWhiteSpace(metadataConnectionString))
        options.UseSqlServer(metadataConnectionString);
    else
        options.UseInMemoryDatabase("integration-framework-dev");
});

// ----- DataProtection: share keys via the metadata store so both API replicas
// can decrypt the OIDC state/correlation cookies (multi-replica SSO login) -----
builder.Services.AddSingleton<IXmlRepository, SqlXmlRepository>();
builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
    new ConfigureOptions<KeyManagementOptions>(o => o.XmlRepository = sp.GetRequiredService<IXmlRepository>()));
builder.Services.AddDataProtection()
    .SetApplicationName("neuro-eptura");

// ----- Engine -----
builder.Services.AddHttpClient("workflow-http");
builder.Services.AddSingleton<IDbClientFactory, DbClientFactory>();
builder.Services.AddSingleton<OAuth2TokenManager>();
builder.Services.AddScoped<IWorkflowNode, TriggerNode>();
builder.Services.AddScoped<IWorkflowNode, HttpRequestNode>();
builder.Services.AddScoped<IWorkflowNode, DbQueryNode>();
builder.Services.AddScoped<IWorkflowNode, TransformNode>();
builder.Services.AddScoped<IWorkflowNode, ConditionNode>();
builder.Services.AddScoped<IWorkflowNode, LoopNode>();
builder.Services.AddScoped<IWorkflowNode, DelayNode>();
builder.Services.AddScoped<IWorkflowNode, EntityMappingNode>();
builder.Services.AddScoped<NodeRegistry>();
builder.Services.AddScoped<WorkflowExecutor>();
builder.Services.AddScoped<WorkflowValidator>();

// ----- Users, roles and sharing -----
builder.Services.AddScoped<IntegrationFramework.Api.Services.CurrentUserService>();
builder.Services.AddScoped<IntegrationFramework.Api.Services.WorkflowAccessService>();

// ----- Demo systems -----
builder.Services.AddSingleton<DemoCrmStore>();
builder.Services.AddSingleton<DemoInventoryStore>();

// ----- Scheduled triggers (worker role) -----
builder.Services.AddHostedService<SchedulerBackgroundService>();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });
builder.Services.AddCors(options => options.AddPolicy("frontend", policy => policy
    .WithOrigins("http://localhost:5173", "http://localhost:8080")
    .AllowAnyHeader()
    .AllowAnyMethod()));

// ----- SSO (corporate Microsoft Entra ID) — enabled when configured, off otherwise -----
var ssoTenantId = builder.Configuration["SSO_TENANT_ID"];
var ssoClientId = builder.Configuration["SSO_CLIENT_ID"];
var ssoClientSecret = builder.Configuration["SSO_CLIENT_SECRET"];
var ssoEnabled = !string.IsNullOrWhiteSpace(ssoTenantId) && !string.IsNullOrWhiteSpace(ssoClientId);

if (ssoEnabled)
{
    builder.Services
        .AddAuthentication(options =>
        {
            options.DefaultScheme = Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.Cookie.Name = "neuro-eptura-auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
            options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
            // Match the legacy DevOpsAutomateHub session behavior: sliding cookie
            // with a configurable timeout (SSO_SESSION_TIMEOUT_MINUTES, default 60).
            var timeoutMinutes = 60;
            if (int.TryParse(builder.Configuration["SSO_SESSION_TIMEOUT_MINUTES"], out var configuredTimeout) && configuredTimeout > 0)
                timeoutMinutes = configuredTimeout;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(timeoutMinutes);
            options.SlidingExpiration = true;
        })
        .AddOpenIdConnect(options =>
        {
            options.Authority = $"https://login.microsoftonline.com/{ssoTenantId}/v2.0";
            options.ClientId = ssoClientId;
            options.CallbackPath = "/auth/callback";
            // The app only needs the authenticated principal (session cookie);
            // storing id/access tokens would inflate the cookie toward nginx's
            // proxy-buffer limits and browser per-cookie size caps.
            options.SaveTokens = false;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.SignInScheme = Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme;
            // Match the legacy DevOpsAutomateHub login mechanism (OWIN OIDC):
            // - no client secret configured → implicit id_token flow, same as the
            //   existing corporate app registration (no secret to request/rotate)
            // - with a secret → authorization code flow (more secure, preferred)
            if (!string.IsNullOrWhiteSpace(ssoClientSecret))
            {
                options.ClientSecret = ssoClientSecret;
                options.ResponseType = Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectResponseType.Code;
            }
            else
            {
                options.ResponseType = Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectResponseType.IdToken;
            }
            // Legacy parity (DevOpsAutomateHub): AJAX calls get a 401 instead of a
            // cross-origin redirect to the identity provider, so XHR/fetch callers
            // can handle the status code instead of dying on a CORS error.
            options.Events.OnRedirectToIdentityProvider = context =>
            {
                if (string.Equals(context.Request.Headers["X-Requested-With"], "XMLHttpRequest",
                        StringComparison.OrdinalIgnoreCase))
                {
                    context.HandleResponse();
                    context.Response.StatusCode = 401;
                    return Task.CompletedTask;
                }
                // Public traffic crosses TLS-terminating proxies that do not forward
                // the original scheme, so the request (and redirect_uri) would be http.
                // Force https for non-local hosts — matches the registered Entra
                // redirect URI and keeps the id_token off plain-http hops.
                if (context.ProtocolMessage?.RedirectUri is string redirectUri
                    && redirectUri.StartsWith("http://", StringComparison.Ordinal)
                    && !context.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    && !context.Request.Host.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                {
                    context.ProtocolMessage.RedirectUri = "https://" + redirectUri["http://".Length..];
                }
                return Task.CompletedTask;
            };
            // Legacy parity: surface the corporate sign-in name (preferred_username)
            // as ClaimTypes.Name so User.Identity.Name shows the user's UPN/email.
            options.Events.OnTokenValidated = context =>
            {
                var preferredUsername = context.Principal?.FindFirst("preferred_username")?.Value;
                if (!string.IsNullOrEmpty(preferredUsername) &&
                    context.Principal?.Identity is System.Security.Claims.ClaimsIdentity identity)
                {
                    identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, preferredUsername));
                }
                return Task.CompletedTask;
            };
        });
}

builder.Services.AddAuthorization(options =>
{
    // With SSO configured, everything requires an authenticated corporate user
    // unless an endpoint opts out with [AllowAnonymous] (webhook, health, demo, auth).
    if (ssoEnabled)
        options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
});

var app = builder.Build();

// ----- Initialize metadata store + seed demo content -----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
    if (db.Database.IsSqlServer())
    {
        // One migrator: the api role applies migrations; workers (same image) skip
        // so concurrent startups don't race on __EFMigrationsHistory.
        var role = builder.Configuration["IF_ROLE"] ?? "api";
        if (role != "worker")
            db.Database.Migrate();
    }
    else
        db.Database.EnsureCreated();

    if (!db.Workflows.Any())
    {
        db.Workflows.Add(new IntegrationFramework.Core.Entities.Workflow
        {
            Name = WorkflowSeeder.SampleWorkflowName,
            Description = "Webhook → transform → mock Inventory reserve. Demonstrates two-system data exchange.",
            GraphJson = WorkflowSeeder.BuildSampleGraph(),
            Enabled = true
        });
        db.SaveChanges();
    }
}

// Behind TLS-terminating proxies (cluster ingress → frontend nginx → API),
// reconstruct the original scheme so OIDC redirect_uri and cookies use https.
var forwardedHeaders = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                     | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
};
forwardedHeaders.KnownNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/", () => Results.Redirect("/health")).AllowAnonymous();

app.Run();

public partial class Program { }

/// <summary>
/// Persists DataProtection key XML in the SQL metadata store so all API replicas
/// share one key ring (required for multi-replica SSO logins: the OIDC state
/// correlation cookie must decrypt on whichever pod receives the callback).
/// </summary>
public class SqlXmlRepository(IServiceProvider services) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        return [.. db.DataProtectionKeys
            .OrderBy(k => k.FriendlyName)
            .Select(k => XElement.Parse(k.Xml!))];
    }

    public void StoreElement(XElement element, string? friendlyName)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
        var xml = element.ToString(SaveOptions.DisableFormatting);
        var existing = db.DataProtectionKeys.SingleOrDefault(k => k.FriendlyName == friendlyName);
        if (existing is null)
            db.DataProtectionKeys.Add(new DataProtectionKey { FriendlyName = friendlyName, Xml = xml });
        else
            existing.Xml = xml;
        db.SaveChanges();
    }
}
