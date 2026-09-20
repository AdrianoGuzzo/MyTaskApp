using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence;

public sealed class MyTaskAppDbContext(DbContextOptions<MyTaskAppDbContext> options)
    : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    public DbSet<TaskOccurrence> Occurrences => Set<TaskOccurrence>();

    internal DbSet<ReminderSettingsRow> ReminderSettings => Set<ReminderSettingsRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MyTaskAppDbContext).Assembly);
}
