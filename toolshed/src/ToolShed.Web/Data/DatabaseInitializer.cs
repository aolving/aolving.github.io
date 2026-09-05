using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

namespace ToolShed.Web.Data;

/// <summary>
/// Creates the schema, the two roles, and the very first admin account — the one
/// account that cannot come from an invitation, because nobody exists to send it.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();

        EnsureDatabaseDirectoryExists(db.Database.GetConnectionString());

        // Swap for db.Database.MigrateAsync() once you have added EF Core migrations
        // (see toolshed/README.md, "Moving to EF Core migrations").
        await db.Database.EnsureCreatedAsync();

        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }

        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        if (await db.Users.AnyAsync())
        {
            return;
        }

        var email = configuration["Seed:AdminEmail"];
        if (string.IsNullOrWhiteSpace(email))
        {
            logger.LogWarning(
                "No members exist and Seed:AdminEmail is not configured, so no administrator was created. " +
                "Set Seed:AdminEmail (and optionally Seed:AdminPassword) and restart to bootstrap the portal.");
            return;
        }

        var password = configuration["Seed:AdminPassword"];
        var generated = string.IsNullOrWhiteSpace(password);
        if (generated)
        {
            password = TokenGenerator.NewPassword();
        }

        var admin = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = configuration["Seed:AdminDisplayName"] ?? "Portal admin",
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(admin, password!);
        if (!result.Succeeded)
        {
            logger.LogError("Could not create the seed administrator: {Errors}",
                string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }

        await userManager.AddToRoleAsync(admin, Roles.Admin);

        if (generated)
        {
            // Printed once, never stored. Sign in and change it immediately.
            logger.LogWarning("Created administrator {Email} with generated password: {Password}", email, password);
        }
        else
        {
            logger.LogInformation("Created administrator {Email} from configuration.", email);
        }
    }

    /// <summary>SQLite will not create a missing folder for its own file, so do it here.</summary>
    private static void EnsureDatabaseDirectoryExists(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:")
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
