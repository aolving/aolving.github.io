using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ToolShed.Web.Data;

/// <summary>
/// Lets `dotnet ef` build the model without starting the web host (and so without
/// running the first-admin bootstrap or touching a real database).
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=App_Data/design-time.db")
            .Options;

        return new ApplicationDbContext(options);
    }
}
