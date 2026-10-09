using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.External;
using MyTaskApp.Application.External.Jira;
using MyTaskApp.Application.History;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registra os casos de uso. Os adaptadores (repositório, unidade de trabalho)
    /// vêm da Infrastructure — esta camada não escolhe implementação.
    /// </summary>
    public static IServiceCollection AddApplication(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        var applicationOptions =
            configuration?.GetSection(ApplicationOptions.SectionName).Get<ApplicationOptions>()
            ?? new ApplicationOptions();

        services.TryAddSingleton(Options.Create(applicationOptions));

        // Relógio real como padrão; testes substituem por FakeTimeProvider.
        services.TryAddSingleton(TimeProvider.System);

        // Sem identificação por padrão. O Desktop registra a conta do sistema
        // antes desta chamada e ganha — TryAdd mantém quem chegou primeiro.
        services.TryAddSingleton<ICurrentUser, UnknownUser>();

        // Singleton: resolver o fuso uma vez basta e evita repetir o fallback.
        services.TryAddSingleton<IUserClock, UserClock>();

        services.AddScoped<GetTodayBoardHandler>();
        services.AddScoped<ReorderOccurrencesHandler>();

        services.AddScoped<CreateTaskHandler>();
        services.AddScoped<QuickCaptureHandler>();
        services.AddScoped<CompleteOccurrenceHandler>();
        services.AddScoped<ReopenOccurrenceHandler>();
        services.AddScoped<CancelOccurrenceHandler>();
        services.AddScoped<UpdateTaskHandler>();
        services.AddScoped<RescheduleOccurrenceHandler>();

        // Etiquetas (ADR-025).
        services.AddScoped<GetTagsHandler>();
        services.AddScoped<CreateTagHandler>();
        services.AddScoped<UpdateTagHandler>();
        services.AddScoped<DeleteTagHandler>();
        services.AddScoped<SetTaskTagsHandler>();
        services.AddScoped<GetTagDirectoriesHandler>();
        services.AddScoped<GetTaskDirectoriesHandler>();
        services.AddScoped<AddTagDirectoryHandler>();
        services.AddScoped<UpdateTagDirectoryHandler>();
        services.AddScoped<RemoveTagDirectoryHandler>();

        // Ambiente de desenvolvimento: Git e worktree da tarefa (ADR-027).
        services.AddScoped<GetTaskDevelopmentsHandler>();
        services.AddScoped<ListEnvironmentFilesHandler>();
        services.AddScoped<ForgetDevelopmentHandler>();
        services.AddScoped<DetectGitHandler>();
        services.AddScoped<InspectDirectoryHandler>();
        services.AddScoped<ListBranchesHandler>();
        services.AddScoped<PrepareDevelopmentHandler>();
        services.AddScoped<StartDevelopmentHandler>();
        services.AddScoped<InspectWorktreeHandler>();
        services.AddScoped<RemoveWorktreeHandler>();
        services.AddScoped<ProbeWorktreesHandler>();

        // A PR aberta da branch, pelo GitHub CLI (ADR-047).
        services.AddScoped<FindPullRequestHandler>();
        services.AddScoped<FindWorktreePullRequestsHandler>();

        // Comandos pós-Worktree e comandos globais por @alias (ADR-028).
        services.AddScoped<GetDevelopmentCommandsHandler>();
        services.AddScoped<CreateDevelopmentCommandHandler>();
        services.AddScoped<UpdateDevelopmentCommandHandler>();
        services.AddScoped<DeleteDevelopmentCommandHandler>();
        services.AddScoped<ValidateCommandEntriesHandler>();
        services.AddScoped<RunCommandHandler>();
        services.AddScoped<SetDevelopmentCommandsHandler>();
        services.AddScoped<RunDevelopmentCommandsHandler>();

        // Comandos rápidos: o global vira botão no diretório da etiqueta (ADR-051).
        services.AddScoped<GetTagDirectoryCommandsHandler>();
        services.AddScoped<AddTagDirectoryCommandHandler>();
        services.AddScoped<CustomizeTagDirectoryCommandHandler>();
        services.AddScoped<SetTagDirectoryCommandEnabledHandler>();
        services.AddScoped<MoveTagDirectoryCommandHandler>();
        services.AddScoped<RemoveTagDirectoryCommandHandler>();
        services.AddScoped<CreateDirectoryOnlyCommandHandler>();
        services.AddScoped<UpdateDirectoryOnlyCommandHandler>();
        services.AddScoped<GetDirectoryOnlyCommandHandler>();
        services.AddScoped<GetQuickCommandsHandler>();
        services.AddScoped<PrepareQuickCommandHandler>();
        services.AddScoped<RunQuickCommandHandler>();
        services.AddScoped<CancelQuickCommandHandler>();
        services.AddScoped<FocusCommandExecutionHandler>();
        services.AddScoped<EndCommandExecutionHandler>();
        services.AddScoped<ReconcileCommandExecutionsHandler>();

        // Sessões de agente de IA (Claude Code) por tarefa (ADR-030). Os
        // agentes em si vêm da Infrastructure; aqui, o catálogo e os casos de uso.
        services.TryAddSingleton<IAgentCliProviders, AgentCliProviders>();
        services.AddScoped<DetectAgentCliHandler>();
        services.AddScoped<GetTaskAgentSessionHandler>();
        services.AddScoped<StartAgentSessionHandler>();
        services.AddScoped<FocusAgentSessionHandler>();
        services.AddScoped<EndAgentSessionHandler>();
        services.AddScoped<ReconcileAgentSessionsHandler>();

        // Os avisos do agente pelos hooks dele (ADR-037). A porta local vem da
        // Infrastructure; o aviso na tela, do Desktop — registrado antes desta
        // chamada, ele ganha do objeto nulo.
        services.AddScoped<RecordAgentEventHandler>();
        services.TryAddSingleton<IAgentAttentionPresenter, NoAgentAttentionPresenter>();

        // O som de cada estado do agente (ADR-042). A biblioteca de sons vem da
        // Infrastructure; quem toca, do Desktop — sem ele, silêncio.
        services.AddScoped<AgentAlertSoundPlayer>();
        services.AddScoped<GetAgentAlertSoundsHandler>();
        services.AddScoped<UpdateAgentAlertSoundHandler>();
        services.AddScoped<ImportSoundHandler>();
        services.AddScoped<DeleteSoundHandler>();
        services.AddScoped<PreviewSoundHandler>();
        services.TryAddSingleton<IAudioPlayer, NoAudioPlayer>();

        // Vínculo com issue de fora — hoje, o Jira (ADR-045). Os provedores e a
        // autenticação vêm da Infrastructure; a busca é singleton porque o cache
        // do autocomplete vale para o app inteiro.
        services.TryAddSingleton<ExternalTaskSearch>();
        services.AddScoped<SearchExternalTasksHandler>();
        services.AddScoped<GetTaskExternalContextHandler>();
        services.AddScoped<LinkTaskToExternalHandler>();
        services.AddScoped<UnlinkTaskFromExternalHandler>();
        services.AddScoped<RefreshExternalTaskHandler>();
        services.AddScoped<GetBranchConventionsHandler>();
        services.AddScoped<UpdateBranchConventionsHandler>();
        services.AddScoped<GetJiraConnectionHandler>();
        services.AddScoped<BeginJiraAuthorizationHandler>();
        services.AddScoped<ChooseJiraSiteHandler>();
        services.AddScoped<ConnectJiraWithApiTokenHandler>();
        services.AddScoped<TestJiraConnectionHandler>();
        services.AddScoped<ListJiraProjectsHandler>();
        services.AddScoped<SetJiraDefaultProjectHandler>();
        services.AddScoped<DisconnectJiraHandler>();

        // Ciclo de vida do checklist: arquivar, lixeira, exclusao definitiva e
        // auditoria (§1 a §8).
        services.AddScoped<ArchiveChecklistHandler>();
        services.AddScoped<RestoreChecklistHandler>();
        services.AddScoped<MoveChecklistToTrashHandler>();
        services.AddScoped<RestoreChecklistFromTrashHandler>();
        services.AddScoped<PurgeChecklistHandler>();
        services.AddScoped<GetChecklistArchiveHandler>();
        services.AddScoped<GetChecklistAuditHandler>();
        services.AddScoped<GetDataRetentionSettingsHandler>();
        services.AddScoped<UpdateDataRetentionSettingsHandler>();
        services.AddScoped<RunLifecycleMaintenanceHandler>();

        services.AddScoped<GetReminderDefaultsHandler>();
        services.AddScoped<UpdateReminderDefaultsHandler>();
        services.AddScoped<SetTaskReminderHandler>();
        services.AddScoped<PauseRemindersHandler>();
        services.AddScoped<ResumeRemindersHandler>();
        services.AddScoped<AcknowledgeReminderHandler>();
        services.AddScoped<SnoozeReminderHandler>();
        services.AddScoped<DispatchDueRemindersHandler>();

        // Prazos (ADR-050). O despacho roda no tique do ReminderScheduler.
        services.AddScoped<SetDeadlineHandler>();
        services.AddScoped<ClearDeadlineHandler>();
        services.AddScoped<SnoozeDeadlineAlertHandler>();
        services.AddScoped<SetTaskDeadlineAlertsHandler>();
        services.AddScoped<UpdateTaskPlanHandler>();
        services.AddScoped<GetDeadlineSettingsHandler>();
        services.AddScoped<UpdateDeadlineSettingsHandler>();
        services.AddScoped<DispatchDeadlineAlertsHandler>();
        services.TryAddSingleton<IDeadlineAlertPresenter, NoDeadlineAlertPresenter>();

        // Tempo trabalhado (ADR-052). Não há laço nem monitor: o cronômetro é o
        // StartedAt gravado, e o relógio da tela é só apresentação.
        services.AddScoped<StartTimerHandler>();
        services.AddScoped<StopTimerHandler>();
        services.AddScoped<GetActiveTimerHandler>();
        services.AddScoped<AddTimeEntryHandler>();
        services.AddScoped<UpdateTimeEntryHandler>();
        services.AddScoped<DeleteTimeEntryHandler>();
        services.AddScoped<GetTaskTimeLogHandler>();

        // O histórico dos últimos dias (ADR-053): só leitura, sobre o que já está gravado.
        services.AddScoped<GetActivityHistoryHandler>();

        // Post-its (ADR-054): o que ainda não merece virar tarefa.
        services.AddScoped<CreateStickyNoteHandler>();
        services.AddScoped<EditStickyNoteHandler>();
        services.AddScoped<ChangeStickyNoteAppearanceHandler>();
        services.AddScoped<PinStickyNoteHandler>();
        services.AddScoped<SetStickyNoteOpenHandler>();
        services.AddScoped<PlaceStickyNoteHandler>();
        services.AddScoped<ArchiveStickyNoteHandler>();
        services.AddScoped<RestoreStickyNoteHandler>();
        services.AddScoped<MoveStickyNoteToTrashHandler>();
        services.AddScoped<RestoreStickyNoteFromTrashHandler>();
        services.AddScoped<PurgeStickyNoteHandler>();
        services.AddScoped<ConvertStickyNoteToTaskHandler>();
        services.AddScoped<GetStickyNotesHandler>();
        services.AddScoped<GetStickyNoteHandler>();
        services.AddScoped<GetStartupStickyNotesHandler>();

        AddDatabaseOperations(services);

        // Singleton: e um laco so, e ele nao pode capturar escopo nenhum
        // (validateScopes: true reprovaria). So recebe IUseCaseRunner.
        services.TryAddSingleton<ReminderScheduler>();

        // Mesmo desenho, cadencia de horas: arquiva o que venceu e esvazia a
        // lixeira vencida. Tambem so recebe IUseCaseRunner, pelo mesmo motivo.
        services.TryAddSingleton<LifecycleMaintenanceScheduler>();

        // O monitor das sessões de agente é o mesmo objeto que os casos de uso
        // avisam: um só dono dos vigias de processo.
        services.TryAddSingleton<AgentSessionMonitor>();
        services.TryAddSingleton<IAgentSessionWatcher>(
            provider => provider.GetRequiredService<AgentSessionMonitor>());

        // O mesmo desenho para os comandos rápidos (ADR-051): o monitor é o
        // dono dos vigias e do registro do que roda neste processo.
        services.TryAddSingleton<CommandExecutionMonitor>();
        services.TryAddSingleton<ICommandExecutionWatcher>(
            provider => provider.GetRequiredService<CommandExecutionMonitor>());

        return services;
    }

    /// <summary>
    /// Operações de banco PostgreSQL (ADR-056). A política e o portão são
    /// singletons sem estado de escopo; o resto vive no escopo da operação.
    /// </summary>
    private static void AddDatabaseOperations(IServiceCollection services)
    {
        services.TryAddSingleton<IDatabaseSecurityPolicy, DatabaseSecurityPolicy>();
        services.TryAddSingleton<DatabaseOperationGate>();

        services.AddScoped<IPostgresEnvironmentDiagnostics, PostgresEnvironmentDiagnostics>();
        services.AddScoped<IPostgresAnonymizationService, PostgresAnonymizationService>();
        services.AddScoped<DatabaseCopyPlanner>();

        services.AddScoped<GetDatabaseConnectionsHandler>();
        services.AddScoped<SaveDatabaseConnectionHandler>();
        services.AddScoped<SetDatabaseConnectionEnabledHandler>();
        services.AddScoped<DeleteDatabaseConnectionHandler>();
        services.AddScoped<TestDatabaseConnectionHandler>();

        services.AddScoped<DetectPostgresToolsHandler>();
        services.AddScoped<DiagnoseDatabaseHandler>();

        services.AddScoped<GetAnonymizationProfilesHandler>();
        services.AddScoped<SaveAnonymizationProfileHandler>();
        services.AddScoped<SetAnonymizationProfileEnabledHandler>();
        services.AddScoped<DeleteAnonymizationProfileHandler>();
        services.AddScoped<SuggestSensitiveColumnsHandler>();
        services.AddScoped<GenerateMaskingScriptHandler>();
        services.AddScoped<ValidateAnonymizationProfileHandler>();

        services.AddScoped<GetDatabaseCopyProfilesHandler>();
        services.AddScoped<SaveDatabaseCopyProfileHandler>();
        services.AddScoped<SetDatabaseCopyProfileEnabledHandler>();
        services.AddScoped<DeleteDatabaseCopyProfileHandler>();

        services.AddScoped<ValidateDatabaseCopyHandler>();
        services.AddScoped<RunDatabaseCopyHandler>();

        services.AddScoped<GetDatabaseOperationHistoryHandler>();
        services.AddScoped<RecoverInterruptedDatabaseOperationsHandler>();
    }
}
