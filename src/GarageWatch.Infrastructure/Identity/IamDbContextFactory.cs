using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GarageWatch.Infrastructure.Identity;

public sealed class IamDbContextFactory : IDesignTimeDbContextFactory<IamDbContext>
{
    public IamDbContext CreateDbContext(string[] args)
    {
        var path = Environment.GetEnvironmentVariable(IdentityBootstrapOptions.DatabasePathKey)
            ?? Path.Combine(Path.GetTempPath(), "garagewatch-iam-design.db");
        var options = new DbContextOptionsBuilder<IamDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new IamDbContext(options);
    }
}
