using Dispatch.Core;
using Dispatch.Core.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// The web site does NOT host a job worker. Long work runs in Dispatch.Worker
// (Windows Service) so IIS recycle / idle timeout cannot kill it.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(DispatchPaths.ConnectionString(builder.Configuration, builder.Environment)));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    DispatchPaths.EnsureSqlite(db);
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
