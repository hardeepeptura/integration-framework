using IntegrationFramework.Api.Demo;
using IntegrationFramework.Connectors.Db;
using IntegrationFramework.Core.Connectors;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Engine;
using IntegrationFramework.Core.Engine.Nodes;
using IntegrationFramework.Worker;
using Microsoft.EntityFrameworkCore;

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
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
        })
        .AddOpenIdConnect(options =>
        {
            options.Authority = $"https://login.microsoftonline.com/{ssoTenantId}/v2.0";
            options.ClientId = ssoClientId;
            options.ClientSecret = ssoClientSecret;
            options.ResponseType = Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectResponseType.Code;
            options.CallbackPath = "/auth/callback";
            options.SaveTokens = true;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.SignInScheme = Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme;
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

app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/", () => Results.Redirect("/health")).AllowAnonymous();

app.Run();

public partial class Program { }
