using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.External;
using MyTaskApp.Application.External.Jira;
using MyTaskApp.Application.History;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.LocalPeers;
using MyTaskApp.Application.Mcp;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Remote.Contracts;

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

        // Um escopo por operação (ADR-012), o mesmo em todo host. Testes de
        // ViewModel registram antes um executor falso e ganham.
        services.TryAddSingleton<IUseCaseRunner, ScopedUseCaseRunner>();

        // Sem identificação por padrão. O Desktop registra a conta do sistema
        // antes desta chamada e ganha — TryAdd mantém quem chegou primeiro.
        services.TryAddSingleton<ICurrentUser, UnknownUser>();

        // Singleton: resolver o fuso uma vez basta e evita repetir o fallback.
        services.TryAddSingleton<IUserClock, UserClock>();

        // Sincronização (ADR-061): a gravação avisa, o agendador espera. Sem
        // conta ligada, o portão de comandos não tem o que barrar.
        services.TryAddSingleton<ISyncTrigger, SyncTrigger>();
        services.TryAddScoped<ICommandReviewGate, NoCommandReviewGate>();
        services.TryAddSingleton<SyncDeviceDirectory>();

        // Onde cada tarefa aparece (ADR-062): quem é "este computador".
        services.TryAddSingleton<DevicePlacementState>();
        services.AddScoped<SettleDevicePlacementsHandler>();
        services.AddScoped<PlaceTaskHandler>();
        services.AddScoped<PlaceTagHandler>();
        services.AddScoped<AcknowledgeArrivalHandler>();

        services.TryAddSingleton<SyncEngine>();
        services.TryAddSingleton<ISyncCycleRunner>(provider => provider.GetRequiredService<SyncEngine>());
        services.TryAddSingleton(provider => new SyncScheduler(
            provider.GetRequiredService<ISyncCycleRunner>(),
            provider.GetRequiredService<ISyncTrigger>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SyncScheduler>>()));
        services.TryAddSingleton<SyncAccountService>();

        // O desktop e o agente no mesmo computador (ADR-085): sem o canal da
        // Infrastructure, ninguém do outro lado.
        services.TryAddSingleton<ILocalPeerChannel, NoLocalPeerChannel>();
        services.TryAddSingleton<LocalPeerBridge>();
        services.TryAddSingleton<ISyncAccount>(provider => provider.GetRequiredService<SyncAccountService>());
        services.AddScoped<ReconcileAfterRemoteChangeHandler>();
        services.AddScoped<RemoteRefetcher>();
        services.AddScoped<GetSyncStatusHandler>();
        services.AddScoped<ListSyncConflictsHandler>();
        services.AddScoped<ResolveSyncConflictHandler>();
        services.AddScoped<ListSyncFailuresHandler>();
        services.AddScoped<RetrySyncOperationHandler>();
        services.AddScoped<DiscardSyncOperationHandler>();
        services.AddScoped<RetryReceivedChangesHandler>();
        services.AddScoped<RetryWaitingReceivedChangesHandler>();
        services.AddScoped<ListCommandReviewsHandler>();
        services.AddScoped<ApproveCommandReviewHandler>();
        services.AddScoped<GetTaskOriginHandler>();

        services.AddScoped<GetTodayBoardHandler>();
        services.AddScoped<ReorderOccurrencesHandler>();

        services.AddScoped<CreateTaskHandler>();
        services.AddScoped<QuickCaptureHandler>();
        services.AddScoped<CompleteOccurrenceHandler>();
        services.AddScoped<ReopenOccurrenceHandler>();
        services.AddScoped<CancelOccurrenceHandler>();
        services.AddScoped<UpdateTaskHandler>();
        services.AddScoped<RescheduleOccurrenceHandler>();

        // Busca e edição em lote, para quem não é a tela — o MCP (ADR-059).
        services.AddScoped<SearchTasksHandler>();
        services.AddScoped<GetTaskDetailsHandler>();
        services.AddScoped<EditTaskHandler>();
        services.AddScoped<CreateDetailedTaskHandler>();
        services.AddScoped<GetTaskStatisticsHandler>();

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

        // Repositório Git do diretório: a URL acompanha a pasta, e o clone (ADR-064).
        services.AddScoped<RepositoryUrlReader>();
        services.AddScoped<ReadRepositoryUrlHandler>();
        services.AddScoped<RefreshTagDirectoryRepositoriesHandler>();
        services.AddScoped<SuggestCloneFolderHandler>();
        services.AddScoped<CloneTagRepositoryHandler>();

        // Ambiente de desenvolvimento: Git e worktree da tarefa (ADR-027).
        services.AddScoped<GetTaskDevelopmentsHandler>();
        services.AddScoped<ListEnvironmentFilesHandler>();
        services.AddScoped<ForgetDevelopmentHandler>();
        services.AddScoped<DetectGitHandler>();
        services.AddScoped<InspectDirectoryHandler>();
        services.AddScoped<ListBranchesHandler>();
        services.AddScoped<PrepareDevelopmentHandler>();
        services.AddScoped<StartDevelopmentHandler>();

        // O mesmo "Iniciar implementação", sem tela (E4): um escopo por etapa, pelo runner.
        services.AddSingleton<ProvisionEnvironmentHandler>();
        services.AddScoped<InspectWorktreeHandler>();
        services.AddScoped<RemoveWorktreeHandler>();
        services.AddScoped<ProbeWorktreesHandler>();

        // Limpeza de worktrees: os sem tarefa e os de tarefa encerrada (ADR-065).
        services.AddScoped<FindWorktreeCleanupHandler>();
        services.AddScoped<CleanWorktreesHandler>();

        // O banco copiado para o ambiente, e o {database} dos comandos (ADR-060).
        services.AddScoped<ValidateDevelopmentDatabaseCopyHandler>();
        services.AddScoped<CopyDevelopmentDatabaseHandler>();

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

        // A Consulta Inteligente (ADR-081): o agente só lendo as pastas e o
        // banco escolhidos. O banco e o preparo do usuário de leitura vêm da
        // Infrastructure; o endereço do MCP, do projeto do MCP — sem ele, a
        // consulta só abre com o código.
        services.AddScoped<ConsultationResolver>();
        services.AddScoped<ConsultationViewFactory>();
        services.AddScoped<ConsultationReadAccess>();
        services.TryAddSingleton<ConsultationReadAccessCache>();

        // O pareamento da orquestração remota, do lado da máquina executora (ADR-087).
        services.AddScoped<GetOpenPairingsHandler>();
        services.AddScoped<ConfirmPairingHandler>();

        // A janela "Máquinas" e o sinal de vida (T-06).
        services.AddScoped<GetRemoteSettingsHandler>();
        services.AddScoped<UpdateRemoteSettingsHandler>();
        services.TryAddSingleton<IAgentAutostart, NoAgentAutostart>();
        services.AddScoped<ApplyAgentAutostartHandler>();
        services.AddScoped<GetMachinesHandler>();
        services.AddScoped<RegisterThisMachineHandler>();
        services.AddScoped<RegisterThisControllerHandler>();
        services.AddScoped<RequestPairingHandler>();
        services.AddScoped<GetPairingStatusHandler>();
        services.AddScoped<RevokeTrustedControllerHandler>();
        services.TryAddSingleton<MachineCapabilities>();
        services.TryAddSingleton<RemotePresenceScheduler>();

        // A fila de operações, do lado do agente (T-14, T-15): a validação local,
        // o despachante, o laço e o que esta versão sabe rodar (ADR-088).
        services.AddScoped<AdmitRemoteOperationHandler>();
        services.AddScoped<GetPendingApprovalsHandler>();
        services.AddScoped<AnswerApprovalHandler>();
        services.AddScoped<SendRemoteOperationHandler>();
        services.AddScoped<GetRemoteControlHandler>();
        services.AddScoped<GetRemoteOperationProgressHandler>();
        services.AddScoped<CancelRemoteOperationHandler>();
        services.TryAddSingleton(RemoteDispatchLimits.Default);
        services.TryAddSingleton<RemoteOperationDispatcher>();
        services.TryAddSingleton<IRemoteHub, NoRemoteHub>();
        services.TryAddSingleton<RemoteOperationScheduler>();
        services.TryAddSingleton<RemoteCatalogPublisher>();

        // Os tipos vão escritos, e não lidos dos executores: o das ferramentas
        // depende das capacidades, que listam os tipos. Um teste confere que batem.
        services.AddSingleton<IRemoteOperationExecutor, EnvironmentInspectExecutor>();
        services.AddSingleton<IRemoteOperationExecutor, CommandRunExecutor>();
        services.AddScoped<RemoteCommandPlanner>();
        services.AddSingleton<IRemoteOperationExecutor, WorktreeProvisionExecutor>();
        services.AddScoped<RemoteProvisionPlanner>();
        services.AddSingleton<IRemoteOperationExecutor, WorktreeRemoveExecutor>();
        services.AddScoped<RemoteRemovalPlanner>();

        // O Claude sem terminal (T-30, ADR-089): o MCP de aprovação é do agente;
        // sem ele, o que perguntaria é negado.
        services.AddSingleton<IRemoteOperationExecutor, AgentStartExecutor>();
        services.AddSingleton<IRemoteOperationExecutor, AgentPromptExecutor>();
        services.AddSingleton<IRemoteOperationExecutor, AgentStopExecutor>();
        services.TryAddSingleton(HeadlessAgentLimits.Default);
        services.TryAddSingleton<HeadlessConversations>();
        services.TryAddSingleton<HeadlessPromptRunner>();
        services.AddScoped<HeadlessAgentPlanner>();
        services.AddScoped<HeadlessSessionStore>();

        // A cópia de banco de longe (T-32, ADR-090): o mesmo caso de uso da tela.
        services.AddSingleton<IRemoteOperationExecutor, DatabaseCopyExecutor>();
        services.AddSingleton<RemoteDatabaseCopy>();
        services.AddScoped<RemoteDatabaseCopyPlanner>();
        services.AddScoped<RecordRemoteOperationHandler>();
        services.TryAddSingleton(new SupportedRemoteOperations([
            RemoteOperationKinds.EnvironmentInspect,
            RemoteOperationKinds.CommandRun,
            RemoteOperationKinds.WorktreeProvision,
            RemoteOperationKinds.WorktreeRemove,
            RemoteOperationKinds.AgentStart,
            RemoteOperationKinds.AgentPrompt,
            RemoteOperationKinds.AgentStop,
            RemoteOperationKinds.DatabaseCopy,
        ]));
        services.AddScoped<ConsultationLauncher>();
        services.AddScoped<ConsultationTargetResolver>();
        services.AddScoped<ConsultationFolders>();
        services.AddScoped<SetConsultationFolderHandler>();
        services.AddScoped<CloneConsultationRepositoryHandler>();
        services.AddScoped<CopyConsultationDatabaseHandler>();
        services.AddScoped<GetTaskConsultationHandler>();
        services.AddScoped<SaveTaskConsultationHandler>();
        services.AddScoped<ClearTaskConsultationHandler>();
        services.AddScoped<PreviewConsultationContextHandler>();
        services.AddScoped<CheckConsultationDatabaseHandler>();
        services.AddScoped<PrepareConsultationReadAccessHandler>();
        services.AddScoped<StartTaskConsultationHandler>();
        services.AddScoped<ResumeTaskConsultationHandler>();
        services.AddScoped<ResolveConsultationAccessHandler>();
        services.AddScoped<DescribeConsultationDatabaseHandler>();
        services.AddScoped<ListConsultationTablesHandler>();
        services.AddScoped<DescribeConsultationTableHandler>();
        services.AddScoped<QueryConsultationDataHandler>();
        services.TryAddSingleton<IConsultationToolsEndpoint, NoConsultationToolsEndpoint>();

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
        services.AddScoped<TimeReportHandlers>();

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

        // O servidor MCP local (ADR-059): a configuração e o token. O aviso
        // de "mudou por fora" é um só para o app inteiro.
        services.TryAddSingleton<IDataChangeNotifier, DataChangeNotifier>();
        services.AddScoped<GetMcpServerSettingsHandler>();
        services.AddScoped<UpdateMcpServerSettingsHandler>();
        services.AddScoped<GetMcpAccessTokenHandler>();
        services.AddScoped<RegenerateMcpAccessTokenHandler>();

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
        services.AddScoped<IMaskingVerifier, MaskingVerifier>();
        services.AddScoped<DatabaseCopyPlanner>();

        services.AddScoped<GetDatabaseConnectionsHandler>();
        services.AddScoped<SaveDatabaseConnectionHandler>();
        services.AddScoped<SetDatabaseConnectionEnabledHandler>();
        services.AddScoped<DeleteDatabaseConnectionHandler>();
        services.AddScoped<TestDatabaseConnectionHandler>();
        services.AddScoped<ListServerDatabasesHandler>();

        services.AddScoped<GetSavedDatabasesHandler>();
        services.AddScoped<SaveSavedDatabaseHandler>();
        services.AddScoped<DeleteSavedDatabaseHandler>();

        services.AddScoped<DetectPostgresToolsHandler>();
        services.AddScoped<DiagnoseDatabaseHandler>();

        services.AddScoped<GetAnonymizationProfilesHandler>();
        services.AddScoped<SaveAnonymizationProfileHandler>();
        services.AddScoped<SetAnonymizationProfileEnabledHandler>();
        services.AddScoped<DeleteAnonymizationProfileHandler>();
        services.AddScoped<SuggestSensitiveColumnsHandler>();
        services.AddScoped<PreviewMaskingHandler>();
        services.AddScoped<GetSourceTablesHandler>();

        // Editar por partes, validar, analisar e comparar (ADR-059).
        services.AddScoped<AnonymizationProfileValidator>();
        services.AddScoped<GetAnonymizationProfileHandler>();
        services.AddScoped<ChangeAnonymizationProfileHandler>();
        services.AddScoped<DuplicateAnonymizationProfileHandler>();
        services.AddScoped<CompareAnonymizationProfilesHandler>();
        services.AddScoped<ValidateAnonymizationProfileHandler>();
        services.AddScoped<AnalyzeAnonymizationProfileHandler>();
        services.AddScoped<DatabaseUsageHandlers>();
        services.AddScoped<DuplicateDatabaseConnectionHandler>();

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
