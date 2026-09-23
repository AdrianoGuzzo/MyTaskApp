using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence;

public sealed class MyTaskAppDbContext(DbContextOptions<MyTaskAppDbContext> options)
    : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    public DbSet<TaskOccurrence> Occurrences => Set<TaskOccurrence>();

    public DbSet<Tag> Tags => Set<Tag>();

    /// <summary>O vínculo N:N entre checklist e etiqueta (ADR-025).</summary>
    public DbSet<TaskItemTag> TaskItemTags => Set<TaskItemTag>();

    /// <summary>As pastas de cada etiqueta, com o alias da anotação (ADR-026).</summary>
    public DbSet<TagDirectory> TagDirectories => Set<TagDirectory>();

    /// <summary>O worktree de cada tarefa, quando houver (ADR-027).</summary>
    public DbSet<TaskDevelopment> TaskDevelopments => Set<TaskDevelopment>();

    /// <summary>Os comandos pós-Worktree de cada ambiente, em ordem (ADR-028).</summary>
    public DbSet<TaskDevelopmentCommand> TaskDevelopmentCommands => Set<TaskDevelopmentCommand>();

    /// <summary>Os comandos globais chamados por <c>@alias</c> (ADR-028).</summary>
    public DbSet<DevelopmentCommand> DevelopmentCommands => Set<DevelopmentCommand>();

    /// <summary>As sessões de agente de IA abertas para as tarefas (ADR-029).</summary>
    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();

    /// <summary>
    /// A trilha de auditoria do ciclo de vida. <b>Não</b> tem relacionamento com
    /// <see cref="Tasks"/>: as linhas precisam sobreviver à exclusão definitiva
    /// do checklist que elas registram (§8).
    /// </summary>
    public DbSet<TaskAuditEntry> TaskAudit => Set<TaskAuditEntry>();

    internal DbSet<ReminderSettingsRow> ReminderSettings => Set<ReminderSettingsRow>();

    internal DbSet<DataRetentionSettingsRow> DataRetentionSettings =>
        Set<DataRetentionSettingsRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MyTaskAppDbContext).Assembly);
}
