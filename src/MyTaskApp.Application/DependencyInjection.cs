using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.External;
using MyTaskApp.Application.External.Jira;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tasks;

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

        return services;
    }
}
