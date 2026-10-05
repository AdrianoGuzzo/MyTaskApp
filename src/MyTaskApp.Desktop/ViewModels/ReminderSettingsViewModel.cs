using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A configuração padrão: o que todo checklist novo recebe. Quem não quiser
/// configurar nada simplesmente não abre esta tela.
/// </summary>
public sealed partial class ReminderSettingsViewModel(
    IUseCaseRunner runner,
    ILogger<ReminderSettingsViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string? _pausedLabel;

    public ReminderEditorViewModel Editor { get; } = new();

    /// <summary>
    /// Os alertas de prazo (ADR-050). Na mesma janela e no mesmo "Salvar":
    /// lembrete e prazo são as duas formas de o app chamar atenção, e o usuário
    /// procura as duas no mesmo lugar.
    /// </summary>
    public DeadlineSettingsEditorViewModel Deadlines { get; } = new();

    /// <summary>Avisa a janela de que pode fechar.</summary>
    public event Action? Saved;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        ReminderSettings? settings = null;
        DeadlineSettings? deadlines = null;

        var loaded = await TryAsync(
            async () =>
            {
                settings = await runner.RunAsync<GetReminderDefaultsHandler, ReminderSettings>(
                    (handler, token) => handler.HandleAsync(new GetReminderDefaults(), token),
                    cancellationToken);

                deadlines = await runner.RunAsync<GetDeadlineSettingsHandler, DeadlineSettings>(
                    (handler, token) => handler.HandleAsync(new GetDeadlineSettings(), token),
                    cancellationToken);
            },
            "Não foi possível carregar a configuração de lembretes.");

        if (!loaded)
        {
            return;
        }

        Editor.Load(settings!.DefaultPolicy);
        Deadlines.Load(deadlines ?? DeadlineSettings.Factory);
        ShowPause(settings.PausedUntilUtc);
        StatusMessage = null;
    }

    [RelayCommand]
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        ReminderPolicy policy;

        try
        {
            policy = Editor.ToPolicy();
        }
        catch (DomainException exception)
        {
            // A combinação é impossível: a mensagem do domínio já está pronta.
            ErrorMessage = exception.Message;
            return;
        }

        var deadlines = Deadlines.ToCommand();

        var saved = await TryAsync(
            async () =>
            {
                // O prazo primeiro: é ele que pode recusar ("escolha pelo menos
                // um aviso"), e a recusa não deve deixar o lembrete já gravado.
                await runner.RunAsync<UpdateDeadlineSettingsHandler, DeadlineSettings>(
                    (handler, token) => handler.HandleAsync(deadlines, token),
                    cancellationToken);

                await runner.RunAsync<UpdateReminderDefaultsHandler>(
                    (handler, token) => handler.HandleAsync(
                        new UpdateReminderDefaults(
                            policy.IsEnabled,
                            policy.Anchor,
                            policy.Offset,
                            policy.RepeatUntilAcknowledged,
                            policy.RepeatEvery,
                            policy.Channels),
                        token),
                    cancellationToken);
            },
            "Não foi possível salvar a configuração.");

        if (saved)
        {
            Saved?.Invoke();
        }
    }

    [RelayCommand]
    public Task RestoreDefaultsAsync(CancellationToken cancellationToken)
    {
        // "Restaurar padrão" lê da mesma constante que a instalação nova usa.
        Editor.Load(ReminderPolicy.Default);
        Deadlines.Load(DeadlineSettings.Factory);
        StatusMessage = "Padrão de fábrica restaurado. Salve para aplicar.";

        return Task.CompletedTask;
    }

    [RelayCommand]
    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        var resumed = await TryAsync(
            () => runner.RunAsync<ResumeRemindersHandler>(
                (handler, token) => handler.HandleAsync(new ResumeReminders(), token),
                cancellationToken),
            "Não foi possível retomar os lembretes.");

        if (resumed)
        {
            ShowPause(null);
        }
    }

    private void ShowPause(DateTimeOffset? pausedUntilUtc)
    {
        IsPaused = pausedUntilUtc is not null;

        PausedLabel = pausedUntilUtc is { } until
            ? $"Lembretes pausados até {until.ToLocalTime():HH:mm}."
            : null;
    }

    /// <summary>Mesmo caminho de erro do resto do app (ADR-008).</summary>
    private async Task<bool> TryAsync(Func<Task> operation, string fallbackMessage)
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await operation();
            return true;
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "ReminderSettingsOperationFailed");
            ErrorMessage = fallbackMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
