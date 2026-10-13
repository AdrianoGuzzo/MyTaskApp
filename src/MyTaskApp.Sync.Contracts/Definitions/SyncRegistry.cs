using static MyTaskApp.Sync.Contracts.Definitions.SyncFields;

namespace MyTaskApp.Sync.Contracts.Definitions;

/// <summary>
/// Todas as entidades que sincronizam, com o que é compartilhado de cada uma
/// (ADR-060). O que não está aqui não sai do computador: caminhos absolutos,
/// estado de janela, lembretes já tocados, processos, conexões e segredos.
/// </summary>
/// <remarks>
/// Os limites repetem as constantes do domínio. O contrato não referencia o
/// domínio de propósito — o servidor não deve depender dele —, e um teste da
/// Infrastructure confere que os dois continuam iguais.
/// </remarks>
public static class SyncRegistry
{
    public static readonly SyncEntityDefinition Task = new(
        SyncEntityTypes.Task,
        rank: 0,
        [
            Text(SyncFields.Task.Title, 200, required: true),
            Text(SyncFields.Task.Description, max: null),
            Integer(SyncFields.Task.Priority, required: true, min: 0, max: 3),
            Text(SyncFields.Task.NextAction, 200),
            new(SyncFields.Task.Estimate, SyncFieldKind.Duration) { Min = 0 },
            new(SyncFields.Task.Reminder, SyncFieldKind.Object)
            {
                IsRequired = true,
                Members =
                [
                    new(Reminder.IsEnabled, SyncFieldKind.Boolean) { IsRequired = true },
                    Integer(Reminder.Anchor, required: true),
                    new(Reminder.Offset, SyncFieldKind.Duration) { IsRequired = true },
                    new(Reminder.Repeat, SyncFieldKind.Boolean) { IsRequired = true },
                    new(Reminder.RepeatEvery, SyncFieldKind.Duration) { IsRequired = true },
                    Integer(Reminder.Channels, required: true),
                ],
            },
            new(SyncFields.Task.External, SyncFieldKind.Object)
            {
                // Duas leituras da mesma issue em horários diferentes não são conflito.
                Policy = SyncConflictPolicy.SilentWhenBothSet,
                Members =
                [
                    Text(External.Provider, 40, required: true),
                    Text(External.Id, 100, required: true),
                    Text(External.Title, 255, required: true),
                    Text(External.Url, 2000, required: true),
                    Text(External.IssueType, 100),
                    Text(External.Status, 100),
                    new(External.SyncedAt, SyncFieldKind.Instant) { IsRequired = true },
                ],
            },
            Integer(SyncFields.Task.DeadlineAlerts),
            Lifecycle(SyncFields.Task.ArchivedAt),
            Lifecycle(SyncFields.Task.DeletedAt),
            Text(SyncFields.Task.DeletedBy, 200) with { Policy = SyncConflictPolicy.SilentWhenBothSet },
            CreatedAtField(),
        ]);

    public static readonly SyncEntityDefinition Tag = new(
        SyncEntityTypes.Tag,
        rank: 0,
        [
            Text(SyncFields.Tag.Name, 40, required: true),
            Text(SyncFields.Tag.ColorHex, 7, required: true),
            CreatedAtField(),
        ])
    {
        UniqueKeyFields = [SyncFields.Tag.Name],
    };

    public static readonly SyncEntityDefinition Occurrence = new(
        SyncEntityTypes.Occurrence,
        rank: 1,
        [
            Parent(SyncFields.Occurrence.TaskId),
            new(SyncFields.Occurrence.Schedule, SyncFieldKind.Object)
            {
                IsRequired = true,
                Members =
                [
                    new(SyncFields.Occurrence.Date, SyncFieldKind.Date),
                    new(SyncFields.Occurrence.Time, SyncFieldKind.Time),
                ],
            },
            new(SyncFields.Occurrence.State, SyncFieldKind.Object)
            {
                IsRequired = true,
                Members =
                [
                    Integer(SyncFields.Occurrence.Status, required: true, min: 0),
                    new(SyncFields.Occurrence.CompletedAt, SyncFieldKind.Instant),
                ],
            },
            new(SyncFields.Occurrence.Deadline, SyncFieldKind.Object)
            {
                Members =
                [
                    new(SyncFields.Occurrence.Date, SyncFieldKind.Date) { IsRequired = true },
                    new(SyncFields.Occurrence.Time, SyncFieldKind.Time),
                ],
            },
        ])
    {
        References = [new(SyncFields.Occurrence.TaskId, SyncEntityTypes.Task, SyncDeleteBehavior.Cascade)],
        TravelsWithParent = true,
    };

    public static readonly SyncEntityDefinition TaskTag = new(
        SyncEntityTypes.TaskTag,
        rank: 1,
        [
            Parent(SyncFields.TaskTag.TaskId),
            Parent(SyncFields.TaskTag.TagId),
        ])
    {
        References =
        [
            new(SyncFields.TaskTag.TaskId, SyncEntityTypes.Task, SyncDeleteBehavior.Cascade),
            new(SyncFields.TaskTag.TagId, SyncEntityTypes.Tag, SyncDeleteBehavior.Cascade),
        ],
        IsAssociation = true,
    };

    public static readonly SyncEntityDefinition TagDirectory = new(
        SyncEntityTypes.TagDirectory,
        rank: 1,
        [
            Parent(SyncFields.TagDirectory.TagId),
            Text(SyncFields.TagDirectory.Alias, 40, required: true),
            Text(SyncFields.TagDirectory.Name, 80),
            Text(SyncFields.TagDirectory.Description, 500),
            Text(SyncFields.TagDirectory.DefaultBranch, 255),
            CreatedAtField(),
        ])
    {
        References = [new(SyncFields.TagDirectory.TagId, SyncEntityTypes.Tag, SyncDeleteBehavior.Cascade)],
        UniqueKeyFields = [SyncFields.TagDirectory.TagId, SyncFields.TagDirectory.Alias],
    };

    public static readonly SyncEntityDefinition Development = new(
        SyncEntityTypes.Development,
        rank: 1,
        [
            Parent(SyncFields.Development.TaskId),
            Text(SyncFields.Development.SourceBranch, 255),
            Text(SyncFields.Development.SourceTag, 255),
            Text(SyncFields.Development.Branch, 255),
            Text(SyncFields.Development.AgentPrompt, 8000),
            new(SyncFields.Development.Commands, SyncFieldKind.List)
            {
                IsRequired = true,
                MaxItems = 50,
                Item = Text("item", 2000, required: true),
            },
            // "Quando esta tentativa começou": criar de novo, em qualquer computador, renova.
            new(CreatedAt, SyncFieldKind.Instant) { IsRequired = true, Policy = SyncConflictPolicy.Silent },
        ])
    {
        References = [new(SyncFields.Development.TaskId, SyncEntityTypes.Task, SyncDeleteBehavior.Cascade)],
    };

    public static readonly SyncEntityDefinition StickyNote = new(
        SyncEntityTypes.StickyNote,
        rank: 1,
        [
            Text(SyncFields.StickyNote.Content, 20_000, required: true),
            new(SyncFields.StickyNote.TagId, SyncFieldKind.Guid),
            new(SyncFields.StickyNote.Appearance, SyncFieldKind.Object)
            {
                IsRequired = true,
                Members =
                [
                    Integer(SyncFields.StickyNote.ColorMode, required: true, min: 0),
                    Integer(SyncFields.StickyNote.PaletteColor, min: 0),
                    Integer(SyncFields.StickyNote.Emphasis, required: true, min: 0),
                    Integer(SyncFields.StickyNote.Opacity, required: true, min: 70, max: 100),
                ],
            },
            Lifecycle(SyncFields.StickyNote.ArchivedAt),
            Lifecycle(SyncFields.StickyNote.DeletedAt),
            // Referência fraca: a tarefa criada a partir do post-it pode ter sido expurgada.
            new(SyncFields.StickyNote.ConvertedTaskId, SyncFieldKind.Guid) { Policy = SyncConflictPolicy.SilentWhenBothSet },
            CreatedAtField(),
            UpdatedAtField(),
        ])
    {
        References = [new(SyncFields.StickyNote.TagId, SyncEntityTypes.Tag, SyncDeleteBehavior.SetNull)],
    };

    public static readonly SyncEntityDefinition Command = new(
        SyncEntityTypes.Command,
        rank: 2,
        [
            new(SyncFields.Command.TagDirectoryId, SyncFieldKind.Guid) { IsImmutable = true },
            // O comando só do diretório (ADR-055) não tem alias.
            Text(SyncFields.Command.Alias, 40),
            Text(SyncFields.Command.Text, 2000, required: true),
            Text(SyncFields.Command.Description, 500),
            Text(SyncFields.Command.Name, 80),
            Integer(SyncFields.Command.Mode, required: true, min: 0),
            Text(SyncFields.Command.WorkingDirectory, 1024),
            new(SyncFields.Command.KeepTerminalOpen, SyncFieldKind.Boolean) { IsRequired = true },
            new(SyncFields.Command.RequiresConfirmation, SyncFieldKind.Boolean) { IsRequired = true },
            new(SyncFields.Command.Parameters, SyncFieldKind.List)
            {
                IsRequired = true,
                MaxItems = 50,
                Item = new("item", SyncFieldKind.Object)
                {
                    IsRequired = true,
                    Members =
                    [
                        new(CommandParameter.Id, SyncFieldKind.Guid) { IsRequired = true },
                        Text(CommandParameter.Name, 64, required: true),
                        Text(CommandParameter.Label, 80),
                        Integer(CommandParameter.Type, required: true, min: 0),
                        Text(CommandParameter.DefaultValue, 500),
                        new(CommandParameter.IsRequired, SyncFieldKind.Boolean) { IsRequired = true },
                        Text(CommandParameter.Options, max: null),
                    ],
                },
            },
            CreatedAtField(),
            UpdatedAtField(),
        ])
    {
        References = [new(SyncFields.Command.TagDirectoryId, SyncEntityTypes.TagDirectory, SyncDeleteBehavior.Cascade)],
        UniqueKeyFields = [SyncFields.Command.Alias],
    };

    public static readonly SyncEntityDefinition TimeEntry = new(
        SyncEntityTypes.TimeEntry,
        rank: 2,
        [
            Parent(SyncFields.TimeEntry.OccurrenceId),
            new(SyncFields.TimeEntry.StartedAt, SyncFieldKind.Instant) { IsRequired = true },
            // Só o período encerrado viaja: o cronômetro rodando é deste computador.
            new(SyncFields.TimeEntry.EndedAt, SyncFieldKind.Instant) { IsRequired = true },
            Integer(SyncFields.TimeEntry.Source, required: true, min: 0) with { IsImmutable = true },
            Text(SyncFields.TimeEntry.Note, 500),
            CreatedAtField(),
            UpdatedAtField(),
        ])
    {
        References = [new(SyncFields.TimeEntry.OccurrenceId, SyncEntityTypes.Occurrence, SyncDeleteBehavior.Cascade)],
    };

    public static readonly SyncEntityDefinition DirectoryCommand = new(
        SyncEntityTypes.DirectoryCommand,
        rank: 3,
        [
            Parent(SyncFields.DirectoryCommand.TagDirectoryId),
            Parent(SyncFields.DirectoryCommand.CommandId),
            Integer(SyncFields.DirectoryCommand.Order, required: true, min: 0),
            new(SyncFields.DirectoryCommand.IsEnabled, SyncFieldKind.Boolean) { IsRequired = true },
            Text(SyncFields.DirectoryCommand.CommandOverride, 2000),
            Text(SyncFields.DirectoryCommand.WorkingDirectoryOverride, 1024),
            CreatedAtField(),
        ])
    {
        References =
        [
            new(SyncFields.DirectoryCommand.TagDirectoryId, SyncEntityTypes.TagDirectory, SyncDeleteBehavior.Cascade),
            new(SyncFields.DirectoryCommand.CommandId, SyncEntityTypes.Command, SyncDeleteBehavior.Cascade),
        ],
        UniqueKeyFields = [SyncFields.DirectoryCommand.TagDirectoryId, SyncFields.DirectoryCommand.CommandId],
    };

    /// <summary>Todas, em ordem de rank: aplicar nesta ordem põe o pai antes do filho.</summary>
    public static IReadOnlyList<SyncEntityDefinition> All { get; } =
        new[] { Task, Tag, Occurrence, TaskTag, TagDirectory, Development, StickyNote, Command, TimeEntry, DirectoryCommand }
            .OrderBy(definition => definition.Rank)
            .ToArray();

    private static readonly Dictionary<string, SyncEntityDefinition> ByType =
        All.ToDictionary(definition => definition.Type, StringComparer.Ordinal);

    /// <summary>O tipo pelo nome no fio, ou <c>null</c> para um tipo que este lado não conhece.</summary>
    public static SyncEntityDefinition? Find(string type) => ByType.GetValueOrDefault(type);

    private static SyncFieldDefinition Text(string name, int? max, bool required = false) =>
        new(name, SyncFieldKind.String) { MaxLength = max, IsRequired = required };

    private static SyncFieldDefinition Integer(string name, bool required = false, long? min = null, long? max = null) =>
        new(name, SyncFieldKind.Integer) { IsRequired = required, Min = min, Max = max };

    private static SyncFieldDefinition Parent(string name) =>
        new(name, SyncFieldKind.Guid) { IsRequired = true, IsImmutable = true };

    private static SyncFieldDefinition Lifecycle(string name) =>
        new(name, SyncFieldKind.Instant) { Policy = SyncConflictPolicy.SilentWhenBothSet };

    private static SyncFieldDefinition CreatedAtField() =>
        new(CreatedAt, SyncFieldKind.Instant) { IsRequired = true, IsImmutable = true };

    private static SyncFieldDefinition UpdatedAtField() =>
        new(UpdatedAt, SyncFieldKind.Instant) { Policy = SyncConflictPolicy.Silent };
}
