using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.External;
using MyTaskApp.Application.External.Jira;
using MyTaskApp.Application.History;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Mcp;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Application.TimeTracking;
using NSubstitute;

namespace MyTaskApp.Application.Tests;

public class ApplicationRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var repository = new FakeTaskItemRepository();
        var processes = new FakeAgentProcessTracker();
        var timeEntries = new FakeTimeEntryRepository();
        var notes = new FakeStickyNoteRepository();
        var database = new DatabaseCopyScenario();

        return new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ITaskItemRepository>(repository)
            .AddSingleton<IUnitOfWork>(repository)
            .AddSingleton<ITodayQuery>(new StubTodayQuery())
            .AddSingleton<IReminderSettingsStore>(new FakeReminderSettingsStore())
            .AddSingleton<IDueReminderQuery>(new StubDueReminderQuery())
            .AddSingleton<ITaskAuditLog>(new FakeTaskAuditLog())
            .AddSingleton<ICurrentUser>(new FakeCurrentUser())
            .AddSingleton<IDataRetentionSettingsStore>(new FakeDataRetentionSettingsStore())
            .AddSingleton<IChecklistArchiveQuery>(new FakeChecklistArchiveQuery())
            .AddSingleton<ILifecycleSweepQuery>(new FakeLifecycleSweepQuery())
            .AddSingleton<ITagRepository>(new FakeTagRepository())
            .AddSingleton<ITagQuery>(new FakeTagQuery())
            .AddSingleton<IAlertPresenter>(new StubAlertPresenter())
            .AddSingleton<ISoundPlayer>(new StubSoundPlayer())
            .AddSingleton<IGitClient>(new FakeGitClient())
            .AddSingleton<IDirectoryProbe>(new FakeDirectoryProbe())
            .AddSingleton<IAgentSessionRepository>(new FakeAgentSessionRepository())
            .AddSingleton<IAgentProcessTracker>(processes)
            .AddSingleton<IDevelopmentCommandRepository>(new FakeDevelopmentCommandRepository())
            .AddSingleton<ICommandExecutor>(new FakeCommandExecutor())
            .AddSingleton<ICommandExecutionRepository>(new FakeCommandExecutionRepository())
            .AddSingleton<ITerminalCommandLauncher>(new FakeTerminalCommandLauncher(processes, DateTimeOffset.UnixEpoch))
            .AddSingleton<ITerminalWindowManager>(new FakeTerminalWindowManager())
            .AddSingleton<ITerminalLauncher>(new FakeTerminalLauncher(new FakeAgentProcessTracker(), DateTimeOffset.UnixEpoch))
            .AddSingleton<IAgentCliProvider>(new FakeAgentCliProvider())
            .AddSingleton(Substitute.For<IHeadlessAgentProvider>())
            .AddSingleton<IProcessOwnership>(new FakeProcessOwnership())
            .AddSingleton(Substitute.For<IHeadlessAgentRunner>())
            .AddSingleton<IAgentSettingsStore>(new FakeAgentSettingsStore())
            .AddSingleton<IAgentAlertSoundStore>(new FakeAgentAlertSoundStore())
            .AddSingleton<ISoundLibrary>(new FakeSoundLibrary())
            .AddSingleton<IAgentEventEndpoint>(new FakeAgentEventEndpoint())
            .AddSingleton<IDirectoryRemover>(new FakeDirectoryRemover())
            .AddSingleton<IBranchConventionStore>(new FakeBranchConventionStore())
            .AddSingleton<IJiraAuthenticationService>(new StubJiraAuthentication())
            .AddSingleton<ITimeEntryRepository>(timeEntries)
            .AddSingleton<IActiveTimerQuery>(new FakeActiveTimerQuery(timeEntries, repository))
            .AddSingleton<IActivityHistoryQuery>(new FakeActivityHistoryQuery())
            .AddSingleton<IStickyNoteRepository>(notes)
            .AddSingleton<IStickyNoteQuery>(new FakeStickyNoteQuery(notes))
            // Operações de banco (ADR-056): cadastro, cofre, ferramentas e servidor.
            .AddSingleton<IDatabaseConnectionRepository>(database.Catalog.Connections)
            .AddSingleton<IAnonymizationProfileRepository>(database.Catalog.AnonymizationProfiles)
            .AddSingleton<IDatabaseCopyProfileRepository>(database.Catalog.CopyProfiles)
            .AddSingleton<ISavedDatabaseRepository>(database.Catalog.SavedDatabases)
            .AddSingleton<IDatabaseOperationAuditLog>(database.Catalog.Audit)
            .AddSingleton<IDatabaseCredentialStore>(database.Credentials)
            .AddSingleton<IPostgresToolLocator>(database.Locator)
            .AddSingleton<IDockerInspector>(database.Docker)
            .AddSingleton<IPostgresServerInspector>(database.Inspector)
            .AddSingleton<IPostgresMaskedCopier>(database.Copier)
            .AddSingleton<IPostgresDumpService>(database.Tools)
            .AddSingleton<IPostgresRestoreService>(database.Tools)
            .AddSingleton<IPostgresScriptRunner>(database.Scripts)
            .AddSingleton<IDatabaseOperationWorkspaceFactory>(database.Workspaces)
            // A Consulta Inteligente (ADR-081): a configuração, o banco só leitura e o preparo do usuário.
            .AddSingleton<ITaskConsultationRepository>(new FakeTaskConsultationRepository())
            // O pareamento da orquestração remota (ADR-087): o servidor, a chave e a confiança local.
            .AddSingleton<MyTaskApp.Application.Remote.IRemoteApi>(new FakeRemoteApi())
            .AddSingleton<MyTaskApp.Application.Remote.IDeviceSigner>(new FakeDeviceSigner())
            .AddSingleton<MyTaskApp.Application.Remote.ITrustedControllerRepository>(new FakeTrustedControllerRepository())
            .AddSingleton<MyTaskApp.Application.Remote.IRemoteSettingsStore>(new FakeRemoteSettingsStore())
            .AddSingleton<MyTaskApp.Application.Remote.IRemoteOperationsApi>(new Remote.FakeOperationQueue())
            .AddSingleton(Substitute.For<MyTaskApp.Application.Remote.IRemoteSessionsApi>())
            .AddSingleton<MyTaskApp.Application.Remote.IRemoteCatalogQuery>(new Remote.FakeCatalogQuery())
            .AddSingleton<MyTaskApp.Application.Remote.IRemoteOperationJournal>(new Remote.InMemoryRemoteJournal())
            .AddSingleton<MyTaskApp.Application.Remote.IRemoteEntityProbe>(new Remote.FakeEntityProbe())
            .AddSingleton(NSubstitute.Substitute.For<MyTaskApp.Application.Sync.ISyncLocalStore>())
            .AddSingleton<IConsultationDatabase>(new FakeConsultationDatabase())
            .AddSingleton<IConsultationReadAccessProvisioner>(new FakeReadAccessProvisioner(new MyTaskApp.Domain.DatabaseOperations.DatabaseSecurityPolicy()))
            // O servidor MCP (ADR-059): configuração, token e as consultas novas.
            .AddSingleton<IMcpServerSettingsStore>(new FakeMcpServerSettingsStore())
            .AddSingleton<IMcpAccessTokenStore>(new FakeMcpAccessTokenStore())
            .AddSingleton<ITaskSearchQuery>(new FakeTaskSearchQuery())
            // A edição em lote chama o SetDeadlineHandler, que lê a configuração de prazos.
            .AddSingleton<IDeadlineSettingsStore>(new FakeDeadlineSettingsStore())
            .AddSingleton<ITimeEntryReportQuery>(new FakeTimeEntryReportQuery())
            // Onde aparece (ADR-062): a identidade e o assentamento vêm da Infrastructure.
            .AddSingleton<MyTaskApp.Application.Sync.IDeviceIdentity>(new FakeDeviceIdentity())
            .AddSingleton<MyTaskApp.Application.Devices.IPendingPlacementQuery>(new FakePendingPlacementQuery())
            // Normalmente vem do composition root do Desktop (ADR-012).
            .AddSingleton<IUseCaseRunner>(new CountingUseCaseRunner())
            .AddApplication()
            .BuildServiceProvider(validateScopes: true);
    }

    [Theory]
    [InlineData(typeof(CreateTaskHandler))]
    [InlineData(typeof(MyTaskApp.Application.Devices.SettleDevicePlacementsHandler))]
    [InlineData(typeof(QuickCaptureHandler))]
    [InlineData(typeof(CompleteOccurrenceHandler))]
    [InlineData(typeof(ReopenOccurrenceHandler))]
    [InlineData(typeof(CancelOccurrenceHandler))]
    [InlineData(typeof(UpdateTaskHandler))]
    [InlineData(typeof(RescheduleOccurrenceHandler))]
    [InlineData(typeof(GetTagsHandler))]
    [InlineData(typeof(CreateTagHandler))]
    [InlineData(typeof(UpdateTagHandler))]
    [InlineData(typeof(DeleteTagHandler))]
    [InlineData(typeof(SetTaskTagsHandler))]
    [InlineData(typeof(GetTagDirectoriesHandler))]
    [InlineData(typeof(GetTaskDirectoriesHandler))]
    [InlineData(typeof(AddTagDirectoryHandler))]
    [InlineData(typeof(UpdateTagDirectoryHandler))]
    [InlineData(typeof(RemoveTagDirectoryHandler))]
    [InlineData(typeof(ReadRepositoryUrlHandler))]
    [InlineData(typeof(RefreshTagDirectoryRepositoriesHandler))]
    [InlineData(typeof(SuggestCloneFolderHandler))]
    [InlineData(typeof(CloneTagRepositoryHandler))]
    [InlineData(typeof(GetTaskDevelopmentsHandler))]
    [InlineData(typeof(GetDevelopmentCommandsHandler))]
    [InlineData(typeof(CreateDevelopmentCommandHandler))]
    [InlineData(typeof(UpdateDevelopmentCommandHandler))]
    [InlineData(typeof(DeleteDevelopmentCommandHandler))]
    [InlineData(typeof(ValidateCommandEntriesHandler))]
    [InlineData(typeof(RunCommandHandler))]
    [InlineData(typeof(SetDevelopmentCommandsHandler))]
    [InlineData(typeof(RunDevelopmentCommandsHandler))]
    [InlineData(typeof(GetTagDirectoryCommandsHandler))]
    [InlineData(typeof(AddTagDirectoryCommandHandler))]
    [InlineData(typeof(CustomizeTagDirectoryCommandHandler))]
    [InlineData(typeof(SetTagDirectoryCommandEnabledHandler))]
    [InlineData(typeof(MoveTagDirectoryCommandHandler))]
    [InlineData(typeof(RemoveTagDirectoryCommandHandler))]
    [InlineData(typeof(CreateDirectoryOnlyCommandHandler))]
    [InlineData(typeof(UpdateDirectoryOnlyCommandHandler))]
    [InlineData(typeof(GetDirectoryOnlyCommandHandler))]
    [InlineData(typeof(GetQuickCommandsHandler))]
    [InlineData(typeof(PrepareQuickCommandHandler))]
    [InlineData(typeof(RunQuickCommandHandler))]
    [InlineData(typeof(CancelQuickCommandHandler))]
    [InlineData(typeof(FocusCommandExecutionHandler))]
    [InlineData(typeof(EndCommandExecutionHandler))]
    [InlineData(typeof(ReconcileCommandExecutionsHandler))]
    [InlineData(typeof(CommandExecutionMonitor))]
    [InlineData(typeof(ICommandExecutionWatcher))]
    [InlineData(typeof(ListEnvironmentFilesHandler))]
    [InlineData(typeof(ForgetDevelopmentHandler))]
    [InlineData(typeof(DetectGitHandler))]
    [InlineData(typeof(InspectDirectoryHandler))]
    [InlineData(typeof(ListBranchesHandler))]
    [InlineData(typeof(PrepareDevelopmentHandler))]
    [InlineData(typeof(StartDevelopmentHandler))]
    [InlineData(typeof(InspectWorktreeHandler))]
    [InlineData(typeof(RemoveWorktreeHandler))]
    [InlineData(typeof(DetectAgentCliHandler))]
    [InlineData(typeof(GetTaskAgentSessionHandler))]
    [InlineData(typeof(StartAgentSessionHandler))]
    [InlineData(typeof(RecordAgentEventHandler))]
    [InlineData(typeof(FocusAgentSessionHandler))]
    [InlineData(typeof(EndAgentSessionHandler))]
    [InlineData(typeof(ReconcileAgentSessionsHandler))]
    [InlineData(typeof(GetTaskConsultationHandler))]
    [InlineData(typeof(SaveTaskConsultationHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.GetOpenPairingsHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.ConfirmPairingHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.GetRemoteSettingsHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.UpdateRemoteSettingsHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.GetMachinesHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.RegisterThisMachineHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.AdmitRemoteOperationHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.RemoteOperationScheduler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.GetPendingApprovalsHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.AnswerApprovalHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.SendRemoteOperationHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.RemoteRemovalPlanner))]
    [InlineData(typeof(MyTaskApp.Application.Remote.HeadlessAgentPlanner))]
    [InlineData(typeof(MyTaskApp.Application.Remote.RemoteDatabaseCopyPlanner))]
    [InlineData(typeof(MyTaskApp.Application.Remote.HeadlessSessionStore))]
    [InlineData(typeof(GetRemotelyOpenedSessionsHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.RemoteProvisionPlanner))]
    [InlineData(typeof(MyTaskApp.Application.Development.ProvisionEnvironmentHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.GetRemoteControlHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.GetRemoteOperationProgressHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.ListRemoteSessionsHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.GetRemoteSessionTimelineHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.RemoteSessionPublisher))]
    [InlineData(typeof(MyTaskApp.Application.Remote.CancelRemoteOperationHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.RemoteCommandPlanner))]
    [InlineData(typeof(MyTaskApp.Application.Remote.RecordRemoteOperationHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.RequestPairingHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.GetPairingStatusHandler))]
    [InlineData(typeof(MyTaskApp.Application.Remote.RevokeTrustedControllerHandler))]
    [InlineData(typeof(ClearTaskConsultationHandler))]
    [InlineData(typeof(PreviewConsultationContextHandler))]
    [InlineData(typeof(CheckConsultationDatabaseHandler))]
    [InlineData(typeof(PrepareConsultationReadAccessHandler))]
    [InlineData(typeof(StartTaskConsultationHandler))]
    [InlineData(typeof(ResumeTaskConsultationHandler))]
    [InlineData(typeof(SetConsultationFolderHandler))]
    [InlineData(typeof(CloneConsultationRepositoryHandler))]
    [InlineData(typeof(CopyConsultationDatabaseHandler))]
    [InlineData(typeof(ResolveConsultationAccessHandler))]
    [InlineData(typeof(DescribeConsultationDatabaseHandler))]
    [InlineData(typeof(ListConsultationTablesHandler))]
    [InlineData(typeof(DescribeConsultationTableHandler))]
    [InlineData(typeof(QueryConsultationDataHandler))]
    [InlineData(typeof(IConsultationToolsEndpoint))]
    [InlineData(typeof(GetAgentAlertSoundsHandler))]
    [InlineData(typeof(UpdateAgentAlertSoundHandler))]
    [InlineData(typeof(ImportSoundHandler))]
    [InlineData(typeof(DeleteSoundHandler))]
    [InlineData(typeof(PreviewSoundHandler))]
    [InlineData(typeof(IAudioPlayer))]
    [InlineData(typeof(AgentSessionMonitor))]
    [InlineData(typeof(IAgentSessionWatcher))]
    [InlineData(typeof(IAgentCliProviders))]
    [InlineData(typeof(ArchiveChecklistHandler))]
    [InlineData(typeof(RestoreChecklistHandler))]
    [InlineData(typeof(MoveChecklistToTrashHandler))]
    [InlineData(typeof(RestoreChecklistFromTrashHandler))]
    [InlineData(typeof(PurgeChecklistHandler))]
    [InlineData(typeof(GetChecklistArchiveHandler))]
    [InlineData(typeof(GetChecklistAuditHandler))]
    [InlineData(typeof(GetDataRetentionSettingsHandler))]
    [InlineData(typeof(UpdateDataRetentionSettingsHandler))]
    [InlineData(typeof(RunLifecycleMaintenanceHandler))]
    [InlineData(typeof(LifecycleMaintenanceScheduler))]
    [InlineData(typeof(GetTodayBoardHandler))]
    [InlineData(typeof(ReorderOccurrencesHandler))]
    [InlineData(typeof(GetReminderDefaultsHandler))]
    [InlineData(typeof(UpdateReminderDefaultsHandler))]
    [InlineData(typeof(SetTaskReminderHandler))]
    [InlineData(typeof(PauseRemindersHandler))]
    [InlineData(typeof(ResumeRemindersHandler))]
    [InlineData(typeof(AcknowledgeReminderHandler))]
    [InlineData(typeof(SnoozeReminderHandler))]
    [InlineData(typeof(DispatchDueRemindersHandler))]
    [InlineData(typeof(ReminderScheduler))]
    [InlineData(typeof(IUserClock))]
    [InlineData(typeof(ExternalTaskSearch))]
    [InlineData(typeof(SearchExternalTasksHandler))]
    [InlineData(typeof(GetTaskExternalContextHandler))]
    [InlineData(typeof(LinkTaskToExternalHandler))]
    [InlineData(typeof(UnlinkTaskFromExternalHandler))]
    [InlineData(typeof(RefreshExternalTaskHandler))]
    [InlineData(typeof(GetBranchConventionsHandler))]
    [InlineData(typeof(UpdateBranchConventionsHandler))]
    [InlineData(typeof(GetJiraConnectionHandler))]
    [InlineData(typeof(BeginJiraAuthorizationHandler))]
    [InlineData(typeof(ChooseJiraSiteHandler))]
    [InlineData(typeof(ConnectJiraWithApiTokenHandler))]
    [InlineData(typeof(TestJiraConnectionHandler))]
    [InlineData(typeof(ListJiraProjectsHandler))]
    [InlineData(typeof(SetJiraDefaultProjectHandler))]
    [InlineData(typeof(DisconnectJiraHandler))]
    [InlineData(typeof(StartTimerHandler))]
    [InlineData(typeof(StopTimerHandler))]
    [InlineData(typeof(GetActiveTimerHandler))]
    [InlineData(typeof(AddTimeEntryHandler))]
    [InlineData(typeof(UpdateTimeEntryHandler))]
    [InlineData(typeof(DeleteTimeEntryHandler))]
    [InlineData(typeof(GetTaskTimeLogHandler))]
    [InlineData(typeof(GetActivityHistoryHandler))]
    [InlineData(typeof(CreateStickyNoteHandler))]
    [InlineData(typeof(EditStickyNoteHandler))]
    [InlineData(typeof(ChangeStickyNoteAppearanceHandler))]
    [InlineData(typeof(PinStickyNoteHandler))]
    [InlineData(typeof(SetStickyNoteOpenHandler))]
    [InlineData(typeof(PlaceStickyNoteHandler))]
    [InlineData(typeof(ArchiveStickyNoteHandler))]
    [InlineData(typeof(RestoreStickyNoteHandler))]
    [InlineData(typeof(MoveStickyNoteToTrashHandler))]
    [InlineData(typeof(RestoreStickyNoteFromTrashHandler))]
    [InlineData(typeof(PurgeStickyNoteHandler))]
    [InlineData(typeof(ConvertStickyNoteToTaskHandler))]
    [InlineData(typeof(GetStickyNotesHandler))]
    [InlineData(typeof(GetStickyNoteHandler))]
    [InlineData(typeof(GetStartupStickyNotesHandler))]
    [InlineData(typeof(GetDatabaseConnectionsHandler))]
    [InlineData(typeof(SaveDatabaseConnectionHandler))]
    [InlineData(typeof(SetDatabaseConnectionEnabledHandler))]
    [InlineData(typeof(DeleteDatabaseConnectionHandler))]
    [InlineData(typeof(TestDatabaseConnectionHandler))]
    [InlineData(typeof(DetectPostgresToolsHandler))]
    [InlineData(typeof(DiagnoseDatabaseHandler))]
    [InlineData(typeof(GetAnonymizationProfilesHandler))]
    [InlineData(typeof(SaveAnonymizationProfileHandler))]
    [InlineData(typeof(SetAnonymizationProfileEnabledHandler))]
    [InlineData(typeof(DeleteAnonymizationProfileHandler))]
    [InlineData(typeof(SuggestSensitiveColumnsHandler))]
    [InlineData(typeof(PreviewMaskingHandler))]
    [InlineData(typeof(GetSourceTablesHandler))]
    [InlineData(typeof(GetDatabaseCopyProfilesHandler))]
    [InlineData(typeof(SaveDatabaseCopyProfileHandler))]
    [InlineData(typeof(SetDatabaseCopyProfileEnabledHandler))]
    [InlineData(typeof(DeleteDatabaseCopyProfileHandler))]
    [InlineData(typeof(ValidateDatabaseCopyHandler))]
    [InlineData(typeof(RunDatabaseCopyHandler))]
    [InlineData(typeof(GetDatabaseOperationHistoryHandler))]
    [InlineData(typeof(RecoverInterruptedDatabaseOperationsHandler))]
    [InlineData(typeof(SearchTasksHandler))]
    [InlineData(typeof(GetTaskDetailsHandler))]
    [InlineData(typeof(EditTaskHandler))]
    [InlineData(typeof(CreateDetailedTaskHandler))]
    [InlineData(typeof(GetTaskStatisticsHandler))]
    [InlineData(typeof(TimeReportHandlers))]
    [InlineData(typeof(GetHoursHistoryHandler))]
    [InlineData(typeof(GetMcpServerSettingsHandler))]
    [InlineData(typeof(UpdateMcpServerSettingsHandler))]
    [InlineData(typeof(GetMcpAccessTokenHandler))]
    [InlineData(typeof(RegenerateMcpAccessTokenHandler))]
    [InlineData(typeof(GetAnonymizationProfileHandler))]
    [InlineData(typeof(ChangeAnonymizationProfileHandler))]
    [InlineData(typeof(DuplicateAnonymizationProfileHandler))]
    [InlineData(typeof(CompareAnonymizationProfilesHandler))]
    [InlineData(typeof(ValidateAnonymizationProfileHandler))]
    [InlineData(typeof(AnalyzeAnonymizationProfileHandler))]
    [InlineData(typeof(DatabaseUsageHandlers))]
    [InlineData(typeof(DuplicateDatabaseConnectionHandler))]
    public void EveryUseCase_CanBeResolved(Type handlerType)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService(handlerType).Should().NotBeNull();
    }

    /// <summary>
    /// Os casos de uso avisam o mesmo monitor que vigia os processos — dois
    /// objetos seriam dois donos dos vigias (ADR-030).
    /// </summary>
    [Fact]
    public void TheSessionWatcher_IsTheMonitorItself()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IAgentSessionWatcher>()
            .Should().BeSameAs(provider.GetRequiredService<AgentSessionMonitor>());
    }

    /// <summary>
    /// O mesmo para os comandos rápidos (ADR-051): o registro do que roda neste
    /// processo só serve se for um só.
    /// </summary>
    [Fact]
    public void TheExecutionWatcher_IsTheMonitorItself()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<ICommandExecutionWatcher>()
            .Should().BeSameAs(provider.GetRequiredService<CommandExecutionMonitor>());
    }

    [Fact]
    public void UserClock_IsRegisteredAsASingletonSoTheTimeZoneIsResolvedOnce()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IUserClock>()
            .Should().BeSameAs(provider.GetRequiredService<IUserClock>());
    }

    private sealed class StubJiraAuthentication : IJiraAuthenticationService
    {
        private static readonly JiraConnection Disconnected = JiraConnection.Disconnected(isOAuthAvailable: false);

        public Task<JiraConnection> GetConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Disconnected);

        public Task<JiraAuthorization> BeginAuthorizationAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JiraConnection> ChooseSiteAsync(string siteId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Disconnected);

        public Task<JiraConnection> ConnectWithApiTokenAsync(
            string siteUrl,
            string email,
            string apiToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Disconnected);

        public Task<JiraConnection> TestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Disconnected);

        public Task<IReadOnlyList<JiraProject>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JiraProject>>([]);

        public Task<JiraConnection> SetDefaultProjectAsync(string? projectKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Disconnected);

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubTodayQuery : ITodayQuery
    {
        public Task<TodayOccurrenceRow?> FindOccurrenceAsync(
            Guid occurrenceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TodayOccurrenceRow?>(null);

        public Task<IReadOnlyList<TodayOccurrenceRow>> GetCandidatesAsync(
            DateOnly today,
            PlacementFilter placement,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TodayOccurrenceRow>>([]);
    }

    private sealed class StubDueReminderQuery : IDueReminderQuery
    {
        public Task<IReadOnlyList<DueReminderRow>> GetDueAsync(
            DateTimeOffset nowUtc,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DueReminderRow>>([]);
    }

    private sealed class StubAlertPresenter : IAlertPresenter
    {
        public Task PresentAsync(ReminderAlert alert, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PresentDigestAsync(
            ReminderDigest digest,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DismissAsync(Guid occurrenceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubSoundPlayer : ISoundPlayer
    {
        public void PlayAlert()
        {
        }
    }

    [Fact]
    public void TimeProvider_IsAvailableSoNoHandlerFallsBackToDateTimeNow()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
    }
}
