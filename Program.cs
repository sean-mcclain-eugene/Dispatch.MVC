using Dispatch.Mvc.Data;
using Dispatch.Mvc.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// SQLite so the demo runs with `dotnet run` — no LocalDB / SQL Server required.
// Swap UseSqlite for UseSqlServer (or Npgsql) in a real app; the rest of the
// pattern (queue, hosted service, detach, inbox) stays the same.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=App_Data/dispatch.db;Cache=Shared";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

// The queue is a singleton Channel. The hosted service is the only consumer.
// Controllers must NOT inject the hosted service — that was the original DI bug.
builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
builder.Services.AddHostedService<QueuedHostedService>();

builder.Services.AddScoped<IJobProcessor, JobProcessor>();
builder.Services.AddScoped<IEmailSender, InboxEmailSender>();

var app = builder.Build();

Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "App_Data"));

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
