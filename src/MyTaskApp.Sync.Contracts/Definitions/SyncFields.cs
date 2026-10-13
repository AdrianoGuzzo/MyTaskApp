namespace MyTaskApp.Sync.Contracts.Definitions;

/// <summary>Os tipos de entidade no fio.</summary>
public static class SyncEntityTypes
{
    public const string Task = "task";

    public const string Occurrence = "occurrence";

    public const string TaskTag = "taskTag";

    public const string Tag = "tag";

    public const string TagDirectory = "tagDirectory";

    public const string Command = "command";

    public const string DirectoryCommand = "directoryCommand";

    public const string Development = "development";

    public const string TimeEntry = "timeEntry";

    public const string StickyNote = "stickyNote";
}

/// <summary>
/// Os nomes dos campos no fio. Constantes, e não texto solto, porque o
/// desktop e o servidor precisam concordar letra por letra.
/// </summary>
public static class SyncFields
{
    public const string CreatedAt = "createdAt";

    public const string UpdatedAt = "updatedAt";

    public static class Task
    {
        public const string Title = "title";
        public const string Description = "description";
        public const string Priority = "priority";
        public const string NextAction = "nextAction";
        public const string Estimate = "estimate";
        public const string Reminder = "reminder";
        public const string External = "external";
        public const string DeadlineAlerts = "deadlineAlerts";
        public const string ArchivedAt = "archivedAt";
        public const string DeletedAt = "deletedAt";
        public const string DeletedBy = "deletedBy";
    }

    public static class Reminder
    {
        public const string IsEnabled = "isEnabled";
        public const string Anchor = "anchor";
        public const string Offset = "offset";
        public const string Repeat = "repeat";
        public const string RepeatEvery = "repeatEvery";
        public const string Channels = "channels";
    }

    public static class External
    {
        public const string Provider = "provider";
        public const string Id = "id";
        public const string Title = "title";
        public const string Url = "url";
        public const string IssueType = "issueType";
        public const string Status = "status";
        public const string SyncedAt = "syncedAt";
    }

    public static class Occurrence
    {
        public const string TaskId = "taskId";
        public const string Schedule = "schedule";
        public const string State = "state";
        public const string Deadline = "deadline";

        public const string Date = "date";
        public const string Time = "time";
        public const string Status = "status";
        public const string CompletedAt = "completedAt";
    }

    public static class TaskTag
    {
        public const string TaskId = "taskId";
        public const string TagId = "tagId";
    }

    public static class Tag
    {
        public const string Name = "name";
        public const string ColorHex = "colorHex";
    }

    public static class TagDirectory
    {
        public const string TagId = "tagId";
        public const string Alias = "alias";
        public const string Name = "name";
        public const string Description = "description";
        public const string DefaultBranch = "defaultBranch";
    }

    public static class Command
    {
        public const string TagDirectoryId = "tagDirectoryId";
        public const string Alias = "alias";
        public const string Text = "command";
        public const string Description = "description";
        public const string Name = "name";
        public const string Mode = "mode";
        public const string WorkingDirectory = "workingDirectory";
        public const string KeepTerminalOpen = "keepTerminalOpen";
        public const string RequiresConfirmation = "requiresConfirmation";
        public const string Parameters = "parameters";
    }

    public static class CommandParameter
    {
        public const string Id = "id";
        public const string Name = "name";
        public const string Label = "label";
        public const string Type = "type";
        public const string DefaultValue = "defaultValue";
        public const string IsRequired = "isRequired";
        public const string Options = "options";
    }

    public static class DirectoryCommand
    {
        public const string TagDirectoryId = "tagDirectoryId";
        public const string CommandId = "developmentCommandId";
        public const string Order = "order";
        public const string IsEnabled = "isEnabled";
        public const string CommandOverride = "commandOverride";
        public const string WorkingDirectoryOverride = "workingDirectoryOverride";
    }

    public static class Development
    {
        public const string TaskId = "taskId";
        public const string SourceBranch = "sourceBranch";
        public const string SourceTag = "sourceTag";
        public const string Branch = "branch";
        public const string AgentPrompt = "agentPrompt";
        public const string Commands = "commands";
    }

    public static class TimeEntry
    {
        public const string OccurrenceId = "occurrenceId";
        public const string StartedAt = "startedAt";
        public const string EndedAt = "endedAt";
        public const string Source = "source";
        public const string Note = "note";
    }

    public static class StickyNote
    {
        public const string Content = "content";
        public const string TagId = "tagId";
        public const string Appearance = "appearance";
        public const string ArchivedAt = "archivedAt";
        public const string DeletedAt = "deletedAt";
        public const string ConvertedTaskId = "convertedTaskId";

        public const string ColorMode = "colorMode";
        public const string PaletteColor = "paletteColor";
        public const string Emphasis = "emphasis";
        public const string Opacity = "opacity";
    }
}
