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
builder.Services.AddScoped<IWorkflowNode, TriggerNode>();
builder.Services.AddScoped<IWorkflowNode, HttpRequestNode>();
builder.Services.AddScoped<IWorkflowNode, DbQueryNode>();
builder.Services.AddScoped<IWorkflowNode, TransformNode>();
builder.Services.AddScoped<IWorkflowNode, ConditionNode>();
builder.Services.AddScoped<IWorkflowNode, LoopNode>();
builder.Services.AddScoped<IWorkflowNode, DelayNode>();
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

var app = builder.Build();

// ----- Initialize metadata store + seed demo content -----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
    if (db.Database.IsSqlServer())
        db.Database.Migrate();
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
app.MapControllers();
app.MapGet("/", () => Results.Redirect("/health"));

app.Run();

public partial class Program { }
