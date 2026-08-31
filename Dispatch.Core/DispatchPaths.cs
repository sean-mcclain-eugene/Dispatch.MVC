using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Dispatch.Core;

/// <summary>
/// Web (IIS) and Worker (Windows Service) must open the <em>same</em> database.
/// Relative <c>App_Data/dispatch.db</c> is rewritten to the repo sibling folder
/// (unzip-and-run). An absolute Data Source, or SQL Server, is left alone so a
/// debug Windows Service can share the web app's sqlite file.
/// </summary>
public static class DispatchPaths
{
    public static string ConnectionString(IConfiguration config, IHostEnvironment env)
    {
        var configured = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = "Data Source=App_Data/dispatch.db;Cache=Shared";
        }

        if (!IsRelativeSqlite(configured))
        {
            EnsureSqliteDirectory(configured);
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

    private static bool IsRelativeSqlite(string connectionString)
    {
        var path = ReadDataSource(connectionString);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return !Path.IsPathRooted(path);
    }

    private static void EnsureSqliteDirectory(string connectionString)
    {
        var path = ReadDataSource(connectionString);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return;
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static string? ReadDataSource(string connectionString)
    {
        const string marker = "Data Source=";
        var i = connectionString.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            return null;
        }

        var rest = connectionString[(i + marker.Length)..];
        var end = rest.IndexOf(';');
        var path = (end < 0 ? rest : rest[..end]).Trim().Trim('"');
        return path;
    }
}
