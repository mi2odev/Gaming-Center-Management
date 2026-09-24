using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GamingCenter.Infrastructure.Data;

/// <summary>Used only by "dotnet ef" to create migrations.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<GamingCenterDbContext>
{
    public GamingCenterDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<GamingCenterDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new GamingCenterDbContext(options);
    }
}
