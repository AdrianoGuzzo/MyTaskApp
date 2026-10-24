using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.StickyNotes;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;
using static MyTaskApp.Sync.Contracts.Definitions.SyncFields;

namespace MyTaskApp.Infrastructure.Sync.Mapping;

/// <summary>Um mapper declarado por tabela: as colunas que viajam e as que ficam.</summary>
internal sealed class TableMapper<TEntity>(
    SyncEntityDefinition definition,
    IReadOnlyList<SyncColumn> columns,
    IReadOnlyList<string> local) : SyncEntityMapper
{
    public override SyncEntityDefinition Definition { get; } = definition;

    public override Type ClrType => typeof(TEntity);

    public override IReadOnlyList<SyncColumn> Columns { get; } = columns;

    public override IReadOnlyList<string> LocalProperties { get; } = local;

    public Dictionary<string, string> Presence { get; init; } = [];

    public override IReadOnlyDictionary<string, string> PresenceProperties => Presence;

    public IReadOnlyList<SyncChildList> Lists { get; init; } = [];

    public override IReadOnlyList<SyncChildList> Children => Lists;

    public Dictionary<string, object?> Defaults { get; init; } = [];

    public override IReadOnlyDictionary<string, object?> InsertDefaults => Defaults;

    public IReadOnlyList<SyncLocalReset> Resets { get; init; } = [];

    public override IReadOnlyList<SyncLocalReset> UpdateResets => Resets;

    public Func<EntityEntry, Guid>? Id { get; init; }

    public Func<EntityEntry, bool>? Syncable { get; init; }

    public Func<EntityEntry, bool>? LocalOnly { get; init; }

    public override Guid IdOf(EntityEntry entry) => Id?.Invoke(entry) ?? base.IdOf(entry);

    public override bool IsSyncable(EntityEntry entry) => Syncable?.Invoke(entry) ?? true;

    public override bool IsLocalOnly(EntityEntry entry) => LocalOnly?.Invoke(entry) ?? false;
}

/// <summary>
/// Todas as tabelas que sincronizam (ADR-061). A lista de "local" é tão
/// importante quanto a de colunas: é ela que diz, por escrito, que o caminho
/// do diretório, a posição na lista e o estado da janela não saem daqui.
/// </summary>
internal static class SyncMappers
{
    private static SyncColumn C(string field, string property) => new(field, null, property);

    private static SyncColumn M(string field, string member, string property) => new(field, member, property);

    public static readonly SyncEntityMapper Task = new TableMapper<TaskItem>(
        SyncRegistry.Task,
        [
            C(SyncFields.Task.Title, nameof(TaskItem.Title)),
            C(SyncFields.Task.Description, nameof(TaskItem.Description)),
            C(SyncFields.Task.Priority, nameof(TaskItem.Priority)),
            C(SyncFields.Task.NextAction, nameof(TaskItem.NextAction)),
            C(SyncFields.Task.Estimate, nameof(TaskItem.Estimate)),
            M(SyncFields.Task.Reminder, Reminder.IsEnabled, "Reminder.IsEnabled"),
            M(SyncFields.Task.Reminder, Reminder.Anchor, "Reminder.Anchor"),
            M(SyncFields.Task.Reminder, Reminder.Offset, "Reminder.Offset"),
            M(SyncFields.Task.Reminder, Reminder.Repeat, "Reminder.RepeatUntilAcknowledged"),
            M(SyncFields.Task.Reminder, Reminder.RepeatEvery, "Reminder.RepeatEvery"),
            M(SyncFields.Task.Reminder, Reminder.Channels, "Reminder.Channels"),
            M(SyncFields.Task.External, External.Provider, "External.Provider"),
            M(SyncFields.Task.External, External.Id, "External.Id"),
            M(SyncFields.Task.External, External.Title, "External.Title"),
            M(SyncFields.Task.External, External.Url, "External.Url"),
            M(SyncFields.Task.External, External.IssueType, "External.IssueType"),
            M(SyncFields.Task.External, External.Status, "External.Status"),
            M(SyncFields.Task.External, External.SyncedAt, "External.SyncedAt"),
            C(SyncFields.Task.DeadlineAlerts, nameof(TaskItem.DeadlineAlerts)),
            C(SyncFields.Task.ArchivedAt, nameof(TaskItem.ArchivedAt)),
            C(SyncFields.Task.DeletedAt, nameof(TaskItem.DeletedAt)),
            C(SyncFields.Task.DeletedBy, nameof(TaskItem.DeletedBy)),
            M(SyncFields.Task.Placement, Placement.Kind, nameof(TaskItem.PlacementKind)),
            M(SyncFields.Task.Placement, Placement.DeviceId, nameof(TaskItem.PlacementDeviceId)),
            C(CreatedAt, nameof(TaskItem.CreatedAt)),
        ],
        [
            // Derivado das ocorrências: cada computador recalcula.
            nameof(TaskItem.ConcludedAt),
            // O destaque de "chegou" é de quem recebeu (ADR-062).
            nameof(TaskItem.ReceivedAt),
            nameof(TaskItem.ReceivedFromDeviceId),
        ])
    {
        Presence =
        {
            [SyncFields.Task.External] = "External.Provider",
            [SyncFields.Task.Placement] = nameof(TaskItem.PlacementKind),
        },
    };

    public static readonly SyncEntityMapper Occurrence = new TableMapper<TaskOccurrence>(
        SyncRegistry.Occurrence,
        [
            C(SyncFields.Occurrence.TaskId, nameof(TaskOccurrence.TaskItemId)),
            M(SyncFields.Occurrence.Schedule, SyncFields.Occurrence.Date, nameof(TaskOccurrence.ScheduledDate)),
            M(SyncFields.Occurrence.Schedule, SyncFields.Occurrence.Time, nameof(TaskOccurrence.ScheduledTime)),
            M(SyncFields.Occurrence.State, SyncFields.Occurrence.Status, nameof(TaskOccurrence.Status)),
            M(SyncFields.Occurrence.State, SyncFields.Occurrence.CompletedAt, nameof(TaskOccurrence.CompletedAt)),
            M(SyncFields.Occurrence.Deadline, SyncFields.Occurrence.Date, nameof(TaskOccurrence.DeadlineDate)),
            M(SyncFields.Occurrence.Deadline, SyncFields.Occurrence.Time, nameof(TaskOccurrence.DeadlineTime)),
        ],
        [
            // A ordem manual é de cada tela; lembretes e avisos de prazo já
            // tocados são deste computador.
            nameof(TaskOccurrence.Position),
            "Reminder.NextFireAtUtc",
            "Reminder.Attempt",
            "Reminder.WaitingSinceUtc",
            "Reminder.LastFiredAtUtc",
            "Reminder.AcknowledgedAtUtc",
            "Reminder.AcknowledgedBy",
            "DeadlineAlert.LastStage",
            "DeadlineAlert.LastAlertAtUtc",
            "DeadlineAlert.SnoozedUntilUtc",
        ])
    {
        Presence = { [SyncFields.Occurrence.Deadline] = nameof(TaskOccurrence.DeadlineDate) },
    };

    public static readonly SyncEntityMapper TaskTag = new TableMapper<TaskItemTag>(
        SyncRegistry.TaskTag,
        [
            C(SyncFields.TaskTag.TaskId, nameof(TaskItemTag.TaskItemId)),
            C(SyncFields.TaskTag.TagId, nameof(TaskItemTag.TagId)),
        ],
        [])
    {
        // O vínculo não tem id no domínio: o do contrato nasce do par.
        Id = entry => SyncIds.TaskTag(
            (Guid)entry.Property(nameof(TaskItemTag.TaskItemId)).CurrentValue!,
            (Guid)entry.Property(nameof(TaskItemTag.TagId)).CurrentValue!),
    };

    public static readonly SyncEntityMapper Tag = new TableMapper<Domain.Tags.Tag>(
        SyncRegistry.Tag,
        [
            C(SyncFields.Tag.Name, nameof(Domain.Tags.Tag.Name)),
            C(SyncFields.Tag.ColorHex, nameof(Domain.Tags.Tag.ColorHex)),
            M(SyncFields.Tag.Placement, Placement.Kind, nameof(Domain.Tags.Tag.PlacementKind)),
            M(SyncFields.Tag.Placement, Placement.DeviceId, nameof(Domain.Tags.Tag.PlacementDeviceId)),
            C(CreatedAt, nameof(Domain.Tags.Tag.CreatedAt)),
        ],
        [])
    {
        Presence = { [SyncFields.Tag.Placement] = nameof(Domain.Tags.Tag.PlacementKind) },
    };

    public static readonly SyncEntityMapper TagDirectory = new TableMapper<Domain.Tags.TagDirectory>(
        SyncRegistry.TagDirectory,
        [
            C(SyncFields.TagDirectory.TagId, nameof(Domain.Tags.TagDirectory.TagId)),
            C(SyncFields.TagDirectory.Alias, nameof(Domain.Tags.TagDirectory.Alias)),
            C(SyncFields.TagDirectory.Name, nameof(Domain.Tags.TagDirectory.Name)),
            C(SyncFields.TagDirectory.Description, nameof(Domain.Tags.TagDirectory.Description)),
            C(SyncFields.TagDirectory.DefaultBranch, nameof(Domain.Tags.TagDirectory.DefaultBranch)),
            C(SyncFields.TagDirectory.RepositoryUrl, nameof(Domain.Tags.TagDirectory.RepositoryUrl)),
            C(CreatedAt, nameof(Domain.Tags.TagDirectory.CreatedAt)),
        ],
        // O mapeamento por dispositivo: o caminho de outra máquina não vale aqui.
        [nameof(Domain.Tags.TagDirectory.Path)]);

    public static readonly SyncEntityMapper DirectoryCommand = new TableMapper<TagDirectoryCommand>(
        SyncRegistry.DirectoryCommand,
        [
            C(SyncFields.DirectoryCommand.TagDirectoryId, nameof(TagDirectoryCommand.TagDirectoryId)),
            C(SyncFields.DirectoryCommand.CommandId, nameof(TagDirectoryCommand.DevelopmentCommandId)),
            C(SyncFields.DirectoryCommand.Order, nameof(TagDirectoryCommand.Order)),
            C(SyncFields.DirectoryCommand.IsEnabled, nameof(TagDirectoryCommand.IsEnabled)),
            C(SyncFields.DirectoryCommand.CommandOverride, nameof(TagDirectoryCommand.CommandOverride)),
            C(SyncFields.DirectoryCommand.WorkingDirectoryOverride, nameof(TagDirectoryCommand.WorkingDirectoryOverride)),
            C(CreatedAt, nameof(TagDirectoryCommand.CreatedAt)),
        ],
        [])
    {
        // O botão de um comando que não é compartilhado fica com ele (ADR-066).
        // Quem garante que o comando está no tracker é a captura.
        LocalOnly = entry => CommandIsLocalOnly(
            entry.Context,
            (Guid)entry.Property(nameof(TagDirectoryCommand.DevelopmentCommandId)).CurrentValue!),
    };

    public static readonly SyncEntityMapper Command = new TableMapper<DevelopmentCommand>(
        SyncRegistry.Command,
        [
            C(SyncFields.Command.TagDirectoryId, nameof(DevelopmentCommand.TagDirectoryId)),
            C(SyncFields.Command.Alias, nameof(DevelopmentCommand.Alias)),
            C(SyncFields.Command.Text, nameof(DevelopmentCommand.Command)),
            C(SyncFields.Command.Description, nameof(DevelopmentCommand.Description)),
            C(SyncFields.Command.Name, nameof(DevelopmentCommand.Name)),
            C(SyncFields.Command.Mode, nameof(DevelopmentCommand.Mode)),
            C(SyncFields.Command.WorkingDirectory, nameof(DevelopmentCommand.WorkingDirectory)),
            C(SyncFields.Command.KeepTerminalOpen, nameof(DevelopmentCommand.KeepTerminalOpen)),
            C(SyncFields.Command.RequiresConfirmation, nameof(DevelopmentCommand.RequiresConfirmation)),
            C(CreatedAt, nameof(DevelopmentCommand.CreatedAt)),
            C(UpdatedAt, nameof(DevelopmentCommand.UpdatedAt)),
        ],
        [
            // Compartilhar é escolha deste computador (ADR-066).
            nameof(DevelopmentCommand.IsShared),

            // Liberar para o controle remoto também (ADR-087): o sync não liga.
            nameof(DevelopmentCommand.RemoteAllowed),
        ])
    {
        LocalOnly = IsLocalOnlyCommand,

        // Mudou o que roda em outro computador: a liberação daqui cai, como na edição local.
        Resets =
        [
            new SyncLocalReset(
                nameof(DevelopmentCommand.RemoteAllowed),
                false,
                [SyncFields.Command.Text, SyncFields.Command.Mode, SyncFields.Command.WorkingDirectory, SyncFields.Command.Parameters]),
        ],

        // O que chega de outro computador foi compartilhado lá.
        Defaults = { [nameof(DevelopmentCommand.IsShared)] = true },

        Lists =
        [
            new SyncChildList(
                SyncFields.Command.Parameters,
                typeof(DevelopmentCommandParameter),
                nameof(DevelopmentCommandParameter.DevelopmentCommandId),
                [
                    nameof(DevelopmentCommandParameter.Name),
                    nameof(DevelopmentCommandParameter.Label),
                    nameof(DevelopmentCommandParameter.Type),
                    nameof(DevelopmentCommandParameter.DefaultValue),
                    nameof(DevelopmentCommandParameter.IsRequired),
                    nameof(DevelopmentCommandParameter.Options),
                    nameof(DevelopmentCommandParameter.Order),
                ],
                [],
                entity => SyncValue.List(((DevelopmentCommand)entity).Parameters
                    .OrderBy(parameter => parameter.Order)
                    .Select(parameter => SyncValue.Object(
                        (CommandParameter.Id, SyncValue.Guid(parameter.Id)),
                        (CommandParameter.Name, SyncValue.String(parameter.Name)),
                        (CommandParameter.Label, SyncValue.String(parameter.Label)),
                        (CommandParameter.Type, SyncValue.Integer((long)parameter.Type)),
                        (CommandParameter.DefaultValue, SyncValue.String(parameter.DefaultValue)),
                        (CommandParameter.IsRequired, SyncValue.Boolean(parameter.IsRequired)),
                        (CommandParameter.Options, SyncValue.String(parameter.Options))))),
                (item, index, commandId) => new Dictionary<string, object?>
                {
                    [nameof(DevelopmentCommandParameter.Id)] = SyncValue.ReadGuid(SyncValue.Member(item, CommandParameter.Id)),
                    [nameof(DevelopmentCommandParameter.DevelopmentCommandId)] = commandId,
                    [nameof(DevelopmentCommandParameter.Name)] = SyncValue.ReadString(SyncValue.Member(item, CommandParameter.Name)),
                    [nameof(DevelopmentCommandParameter.Label)] = SyncValue.ReadString(SyncValue.Member(item, CommandParameter.Label)),
                    [nameof(DevelopmentCommandParameter.Type)] =
                        (CommandParameterType)SyncValue.ReadInteger(SyncValue.Member(item, CommandParameter.Type))!.Value,
                    [nameof(DevelopmentCommandParameter.DefaultValue)] = SyncValue.ReadString(SyncValue.Member(item, CommandParameter.DefaultValue)),
                    [nameof(DevelopmentCommandParameter.IsRequired)] = SyncValue.ReadBoolean(SyncValue.Member(item, CommandParameter.IsRequired)),
                    [nameof(DevelopmentCommandParameter.Options)] = SyncValue.ReadString(SyncValue.Member(item, CommandParameter.Options)),
                    [nameof(DevelopmentCommandParameter.Order)] = index,
                }),
        ],
    };

    public static readonly SyncEntityMapper Development = new TableMapper<TaskDevelopment>(
        SyncRegistry.Development,
        [
            C(SyncFields.Development.TaskId, nameof(TaskDevelopment.TaskItemId)),
            C(SyncFields.Development.SourceBranch, nameof(TaskDevelopment.SourceBranch)),
            C(SyncFields.Development.SourceTag, nameof(TaskDevelopment.SourceTag)),
            C(SyncFields.Development.Branch, nameof(TaskDevelopment.Branch)),
            C(SyncFields.Development.AgentPrompt, nameof(TaskDevelopment.AgentPrompt)),
            C(CreatedAt, nameof(TaskDevelopment.CreatedAt)),
        ],
        [
            // O repositório e o worktree são pastas deste computador; o estado
            // diz se o worktree existe aqui.
            nameof(TaskDevelopment.RepositoryPath),
            nameof(TaskDevelopment.WorktreePath),
            nameof(TaskDevelopment.Status),
            nameof(TaskDevelopment.StatusChangedAt),
            nameof(TaskDevelopment.FailureReason),

            // O banco copiado (ADR-060) está num servidor visto deste computador.
            "Database.ConnectionId",
            "Database.ConnectionName",
            "Database.Host",
            "Database.Port",
            "Database.Username",
            "Database.Name",
            "Database.AuditId",
            "Database.CopiedAt",
            "Database.Alias",
        ])
    {
        Lists =
        [
            new SyncChildList(
                SyncFields.Development.Commands,
                typeof(TaskDevelopmentCommand),
                nameof(TaskDevelopmentCommand.TaskDevelopmentId),
                [nameof(TaskDevelopmentCommand.Command), nameof(TaskDevelopmentCommand.Order)],
                [],
                entity => SyncValue.List(((TaskDevelopment)entity).Commands.Select(command => SyncValue.String(command.Command))),
                (item, index, developmentId) => new Dictionary<string, object?>
                {
                    // A linha filha não tem identidade no fio: é a posição na lista.
                    [nameof(TaskDevelopmentCommand.Id)] = Guid.CreateVersion7(),
                    [nameof(TaskDevelopmentCommand.TaskDevelopmentId)] = developmentId,
                    [nameof(TaskDevelopmentCommand.Command)] = SyncValue.ReadString(item),
                    [nameof(TaskDevelopmentCommand.Order)] = index,
                }),
        ],

        // Veio de outro computador: sem repositório nem worktree aqui até "Criar aqui".
        Defaults =
        {
            [nameof(TaskDevelopment.RepositoryPath)] = string.Empty,
            [nameof(TaskDevelopment.WorktreePath)] = string.Empty,
            [nameof(TaskDevelopment.Status)] = TaskDevelopmentStatus.NotCreatedHere,
        },
    };

    public static readonly SyncEntityMapper TimeEntry = new TableMapper<Domain.TimeTracking.TimeEntry>(
        SyncRegistry.TimeEntry,
        [
            C(SyncFields.TimeEntry.OccurrenceId, nameof(Domain.TimeTracking.TimeEntry.TaskOccurrenceId)),
            C(SyncFields.TimeEntry.StartedAt, nameof(Domain.TimeTracking.TimeEntry.StartedAt)),
            C(SyncFields.TimeEntry.EndedAt, nameof(Domain.TimeTracking.TimeEntry.EndedAt)),
            C(SyncFields.TimeEntry.Source, nameof(Domain.TimeTracking.TimeEntry.Source)),
            C(SyncFields.TimeEntry.Note, nameof(Domain.TimeTracking.TimeEntry.Note)),
            C(CreatedAt, nameof(Domain.TimeTracking.TimeEntry.CreatedAt)),
            C(UpdatedAt, nameof(Domain.TimeTracking.TimeEntry.UpdatedAt)),
        ],
        [])
    {
        // O cronômetro rodando é deste computador: viaja quando parar.
        Syncable = entry => entry.Property(nameof(Domain.TimeTracking.TimeEntry.EndedAt)).CurrentValue is not null,
    };

    public static readonly SyncEntityMapper StickyNote = new TableMapper<Domain.StickyNotes.StickyNote>(
        SyncRegistry.StickyNote,
        [
            C(SyncFields.StickyNote.Content, nameof(Domain.StickyNotes.StickyNote.Content)),
            C(SyncFields.StickyNote.TagId, nameof(Domain.StickyNotes.StickyNote.TagId)),
            M(SyncFields.StickyNote.Appearance, SyncFields.StickyNote.ColorMode, nameof(Domain.StickyNotes.StickyNote.ColorMode)),
            M(SyncFields.StickyNote.Appearance, SyncFields.StickyNote.PaletteColor, nameof(Domain.StickyNotes.StickyNote.PaletteColor)),
            M(SyncFields.StickyNote.Appearance, SyncFields.StickyNote.Emphasis, nameof(Domain.StickyNotes.StickyNote.Emphasis)),
            M(SyncFields.StickyNote.Appearance, SyncFields.StickyNote.Opacity, nameof(Domain.StickyNotes.StickyNote.Opacity)),
            C(SyncFields.StickyNote.ArchivedAt, nameof(Domain.StickyNotes.StickyNote.ArchivedAt)),
            C(SyncFields.StickyNote.DeletedAt, nameof(Domain.StickyNotes.StickyNote.DeletedAt)),
            C(SyncFields.StickyNote.ConvertedTaskId, nameof(Domain.StickyNotes.StickyNote.ConvertedTaskId)),
            C(CreatedAt, nameof(Domain.StickyNotes.StickyNote.CreatedAt)),
            C(UpdatedAt, nameof(Domain.StickyNotes.StickyNote.UpdatedAt)),
        ],
        [
            // A janela do post-it é desta tela.
            nameof(Domain.StickyNotes.StickyNote.IsOpen),
            nameof(Domain.StickyNotes.StickyNote.IsPinned),
            nameof(Domain.StickyNotes.StickyNote.X),
            nameof(Domain.StickyNotes.StickyNote.Y),
            nameof(Domain.StickyNotes.StickyNote.Width),
            nameof(Domain.StickyNotes.StickyNote.Height),
        ])
    {
        // Um post-it criado em outro computador não abre janela aqui sozinho.
        Defaults =
        {
            [nameof(Domain.StickyNotes.StickyNote.IsOpen)] = false,
            [nameof(Domain.StickyNotes.StickyNote.Width)] = Domain.StickyNotes.StickyNote.DefaultWidth,
            [nameof(Domain.StickyNotes.StickyNote.Height)] = Domain.StickyNotes.StickyNote.DefaultHeight,
        },
    };

    /// <summary>O comando global que este computador não compartilha (ADR-066).</summary>
    public static bool IsLocalOnlyCommand(EntityEntry entry) =>
        entry.Property(nameof(DevelopmentCommand.TagDirectoryId)).CurrentValue is null
        && !(bool)entry.Property(nameof(DevelopmentCommand.IsShared)).CurrentValue!;

    /// <summary>Pelo tracker: o comando fora dele conta como compartilhado.</summary>
    public static bool CommandIsLocalOnly(DbContext context, Guid commandId) =>
        context.Set<DevelopmentCommand>().Local.FindEntry(commandId) is { } entry && IsLocalOnlyCommand(entry);

    /// <summary>Em ordem de rank, como o registro do contrato.</summary>
    public static IReadOnlyList<SyncEntityMapper> All { get; } =
        new[] { Task, Tag, Occurrence, TaskTag, TagDirectory, Development, StickyNote, Command, TimeEntry, DirectoryCommand }
            .OrderBy(mapper => mapper.Definition.Rank)
            .ToArray();

    private static readonly Dictionary<Type, SyncEntityMapper> ByClrType = All.ToDictionary(mapper => mapper.ClrType);

    private static readonly Dictionary<string, SyncEntityMapper> ByEntityType =
        All.ToDictionary(mapper => mapper.Definition.Type, StringComparer.Ordinal);

    private static readonly Dictionary<Type, (SyncEntityMapper Parent, SyncChildList List)> ByChildType =
        All.SelectMany(mapper => mapper.Children.Select(child => (child.ChildType, (mapper, child))))
            .ToDictionary(pair => pair.ChildType, pair => pair.Item2);

    public static SyncEntityMapper? ForClrType(Type type) => ByClrType.GetValueOrDefault(type);

    public static SyncEntityMapper? ForEntityType(string type) => ByEntityType.GetValueOrDefault(type);

    /// <summary>A linha filha que viaja dentro de um campo do pai.</summary>
    public static (SyncEntityMapper Parent, SyncChildList List)? ForChildType(Type type) =>
        ByChildType.TryGetValue(type, out var found) ? found : null;
}
