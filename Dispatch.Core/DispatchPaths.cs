using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Dispatch.Core;

/// <summary>
/// Web (IIS) and Worker (Windows Service) must open the <em>same</em> database.
/// Relative <c>../App_Data</c> is the unzip-and-run default. In production
/// point both appsettings at the same SQL Server catalog instead.
/// </summary>
public static class DispatchPaths
{
    public static string ConnectionString(IConfiguration config, IHostEnvironment env)
    {
        var configured = config.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(configured) &&
            !configured.Contains("App_Data/dispatch.db", StringComparison.OrdinalIgnoreCase) &&
            !configured.Contains("App_Data\\dispatch.db", StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        var dataDir = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "App_Data"));
        Directory.CreateDirectory(dataDir);
        var file = Path.Combine(dataDir, "dispatch.db");
        return $"Data Source={file};Cache=Shared";
    }

    public static void EnsureSqlite(Microsoft.EntityFrameworkCore.DbContext db)
    {
        db.Database.EnsureCreated();
        try
        {
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
        }
        catch (Exception)
        {
            // SQL Server / other providers: the PRAGMA is meaningless.
        }
    }
}
