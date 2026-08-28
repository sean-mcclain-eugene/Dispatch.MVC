using Dispatch.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Core.Data;

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
            e.HasIndex(j => new { j.Status, j.LockUntilUtc });
        });

        modelBuilder.Entity<InboxMessage>(e =>
        {
            e.HasIndex(m => m.SentUtc);
            e.HasIndex(m => m.IsRead);
        });
    }
}
