using Dispatch.Mvc.Models;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Mvc.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<LongRunningJob> Jobs => Set<LongRunningJob>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LongRunningJob>(e =>
        {
            e.HasIndex(j => j.CreatedUtc);
            e.HasIndex(j => j.NotifyEmail);
        });

        modelBuilder.Entity<InboxMessage>(e =>
        {
            e.HasIndex(m => m.SentUtc);
            e.HasIndex(m => m.IsRead);
        });
    }
}
