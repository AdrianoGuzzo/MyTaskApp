using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
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

        return services;
    }
}
