using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IntegrationFramework.Core.Data;

/// <summary>
/// Design-time factory so `dotnet ef migrations` always builds the model against the
/// SQL Server provider. Program.cs falls back to the InMemory provider when no
/// connection string is configured, and migrations cannot be generated on InMemory.
/// The connection string is never used at design time (no connection is opened).
/// </summary>
public class MetadataDbContextDesignFactory : IDesignTimeDbContextFactory<MetadataDbContext>
{
    public MetadataDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MetadataDbContext>()
            .UseSqlServer("Server=localhost;Database=DesignTime;TrustServerCertificate=True")
            .Options);
}
