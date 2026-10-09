using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.StickyNotes;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;

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

    /// <summary>Como perguntar os <c>{nome}</c> de cada comando global (ADR-051).</summary>
    public DbSet<DevelopmentCommandParameter> DevelopmentCommandParameters => Set<DevelopmentCommandParameter>();

    /// <summary>Os comandos rápidos de cada diretório de etiqueta, em ordem (ADR-051).</summary>
    public DbSet<TagDirectoryCommand> TagDirectoryCommands => Set<TagDirectoryCommand>();

    /// <summary>O histórico dos comandos rápidos executados (ADR-051).</summary>
    public DbSet<CommandExecution> CommandExecutions => Set<CommandExecution>();

    /// <summary>As sessões de agente de IA abertas para as tarefas (ADR-030).</summary>
    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();

    /// <summary>Os períodos de trabalho de cada ocorrência (ADR-052).</summary>
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();

    /// <summary>Os post-its: o que ainda não virou tarefa (ADR-054).</summary>
    public DbSet<StickyNote> StickyNotes => Set<StickyNote>();

    /// <summary>
    /// A trilha de auditoria do ciclo de vida. <b>Não</b> tem relacionamento com
    /// <see cref="Tasks"/>: as linhas precisam sobreviver à exclusão definitiva
    /// do checklist que elas registram (§8).
    /// </summary>
    public DbSet<TaskAuditEntry> TaskAudit => Set<TaskAuditEntry>();

    /// <summary>As conexões PostgreSQL cadastradas, sem senha (ADR-056).</summary>
    public DbSet<DatabaseConnection> DatabaseConnections => Set<DatabaseConnection>();

    /// <summary>Os perfis de anonimização e as regras confirmadas (ADR-056).</summary>
    public DbSet<AnonymizationProfile> AnonymizationProfiles => Set<AnonymizationProfile>();

    /// <summary>As cópias que se repetem (ADR-056).</summary>
    public DbSet<DatabaseCopyProfile> DatabaseCopyProfiles => Set<DatabaseCopyProfile>();

    /// <summary>Os apelidos de banco de origem: conexão, banco e anonimização (ADR-057).</summary>
    public DbSet<SavedDatabase> SavedDatabases => Set<SavedDatabase>();

    /// <summary>A trilha das operações de banco, sem chave estrangeira (ADR-056).</summary>
    public DbSet<DatabaseOperationAudit> DatabaseOperationAudits => Set<DatabaseOperationAudit>();

    internal DbSet<ReminderSettingsRow> ReminderSettings => Set<ReminderSettingsRow>();

    internal DbSet<DataRetentionSettingsRow> DataRetentionSettings =>
        Set<DataRetentionSettingsRow>();

    /// <summary>Os parâmetros com que cada agente abre (ADR-030).</summary>
    internal DbSet<AgentSettingsRow> AgentSettings => Set<AgentSettingsRow>();

    /// <summary>O som de cada estado do agente que avisa (ADR-042).</summary>
    internal DbSet<AgentAlertSoundRow> AgentAlertSounds => Set<AgentAlertSoundRow>();

    /// <summary>As convenções de nome de branch das tarefas vinculadas (ADR-045).</summary>
    internal DbSet<BranchSettingsRow> BranchSettings => Set<BranchSettingsRow>();

    /// <summary>Os avisos de prazo e o horário padrão do prazo (ADR-050).</summary>
    internal DbSet<DeadlineSettingsRow> DeadlineSettings => Set<DeadlineSettingsRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MyTaskAppDbContext).Assembly);
}
