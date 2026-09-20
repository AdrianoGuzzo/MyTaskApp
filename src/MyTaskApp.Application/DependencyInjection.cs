using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
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

        // Singleton: resolver o fuso uma vez basta e evita repetir o fallback.
        services.TryAddSingleton<IUserClock, UserClock>();

        services.AddScoped<GetTodayBoardHandler>();

        services.AddScoped<CreateTaskHandler>();
        services.AddScoped<QuickCaptureHandler>();
        services.AddScoped<CompleteOccurrenceHandler>();
        services.AddScoped<ReopenOccurrenceHandler>();
        services.AddScoped<CancelOccurrenceHandler>();
        services.AddScoped<UpdateTaskHandler>();
        services.AddScoped<RescheduleOccurrenceHandler>();
        services.AddScoped<DeleteTaskHandler>();

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

        return services;
    }
}
