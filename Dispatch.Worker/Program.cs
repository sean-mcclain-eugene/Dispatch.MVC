using Dispatch.Core;
using Dispatch.Core.Data;
using Dispatch.Core.Services;
using Dispatch.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;

// Windows Services start with cwd = System32. Pin it to the publish folder
// so relative files (if any) and logging land next to the exe.
if (WindowsServiceHelpers.IsWindowsService())
{
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "DispatchWorker";
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(DispatchPaths.ConnectionString(builder.Configuration, builder.Environment)));

builder.Services.AddScoped<IJobProcessor, JobProcessor>();
builder.Services.AddScoped<IEmailSender, InboxEmailSender>();
builder.Services.AddHostedService<JobWorker>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    DispatchPaths.EnsureSqlite(db);
}

host.Run();
