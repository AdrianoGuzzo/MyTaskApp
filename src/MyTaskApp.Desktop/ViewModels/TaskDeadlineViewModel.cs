using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Como a tarefa é avisada do prazo (§20).</summary>
public enum DeadlineAlertMode
{
    /// <summary>Segue a configuração global, viva.</summary>
    Default,

    /// <summary>Nenhum aviso: o prazo só aparece na lista.</summary>
    Silent,

    /// <summary>A véspera e o atraso, e nada mais.</summary>
    DayBefore,

    /// <summary>Os degraus escolhidos aqui.</summary>
    Custom,
}

/// <summary>
/// O card "Prazo" da tarefa (ADR-050, §8, §14, §15, §20): o prazo e quanto
/// falta, os atalhos e o "personalizado", os avisos desta tarefa, a próxima
/// ação e a estimativa.
/// </summary>
/// <remarks>
/// Grava cada coisa quando acontece, como o card do Jira: o "Salvar" da janela
/// é da anotação, e um prazo escolhido não pode depender dele. Nada de prazo é
/// calculado aqui — os textos vêm prontos da Application (<see cref="TaskDeadlineView"/>).
/// </remarks>
public sealed partial class TaskDeadlineViewModel(
    IUseCaseRunner runner,
    ILogger<TaskDeadlineViewModel> logger) : ObservableObject
{
    private Guid _taskId;
    private Guid _occurrenceId;
    private string? _persistedNextAction;
    private TimeSpan? _persistedEstimate;

    /// <summary>Enquanto a carga preenche os campos, mexer neles não é o usuário pedindo nada.</summary>
    private bool _loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeadline))]
    [NotifyPropertyChangedFor(nameof(DateLabel))]
    [NotifyPropertyChangedFor(nameof(Countdown))]
    [NotifyPropertyChangedFor(nameof(IsAttention))]
    [NotifyPropertyChangedFor(nameof(IsUrgent))]
    [NotifyPropertyChangedFor(nameof(IsOverdue))]
    [NotifyPropertyChangedFor(nameof(EditLabel))]
    [NotifyPropertyChangedFor(nameof(ShowsPlan))]
    private TaskDeadlineView? _view;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    private bool _isReadOnly;

    /// <summary>O painel de escolha (atalhos, dia e hora) está aberto.</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>O dia do "personalizado". <c>DateTime</c> porque é o que o <c>CalendarDatePicker</c> fala.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCustomCommand))]
    private DateTime? _customDate;

    /// <summary>A hora do "personalizado". <c>TimeSpan</c> porque é o que o <c>TimePicker</c> fala.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCustomCommand))]
    private TimeSpan? _customTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDefaultAlerts))]
    [NotifyPropertyChangedFor(nameof(IsSilentAlerts))]
    [NotifyPropertyChangedFor(nameof(IsDayBeforeAlerts))]
    [NotifyPropertyChangedFor(nameof(IsCustomAlerts))]
    private DeadlineAlertMode _alertMode;

    [ObservableProperty]
    private bool _alertSevenDays;

    [ObservableProperty]
    private bool _alertThreeDays;

    [ObservableProperty]
    private bool _alertOneDay;

    [ObservableProperty]
    private bool _alertEightHours;

    [ObservableProperty]
    private bool _alertTwoHours;

    [ObservableProperty]
    private bool _alertOverdue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanChanges))]
    [NotifyPropertyChangedFor(nameof(ShowsPlan))]
    [NotifyCanExecuteChangedFor(nameof(SavePlanCommand))]
    private string _nextAction = string.Empty;

    /// <summary>Em horas, que é como se estima trabalho: "6", "1,5".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanChanges))]
    [NotifyPropertyChangedFor(nameof(ShowsPlan))]
    [NotifyCanExecuteChangedFor(nameof(SavePlanCommand))]
    private decimal? _estimateHours;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>A lista desenha o prazo: qualquer mudança aqui a recarrega.</summary>
    public event Action? Changed;

    public bool HasDeadline => View is not null;

    public bool IsEditable => !IsReadOnly;

    public string DateLabel => View?.DateLabel ?? string.Empty;

    public string Countdown => View?.Countdown ?? string.Empty;

    public bool IsAttention => View?.Severity == DeadlineSeverity.Attention && !IsReadOnly;

    public bool IsUrgent => View?.Severity == DeadlineSeverity.Urgent && !IsReadOnly;

    public bool IsOverdue => View?.Severity == DeadlineSeverity.Overdue && !IsReadOnly;

    public string EditLabel => HasDeadline ? "Alterar" : "Definir prazo";

    // Um booleano por opção, para os RadioButton: marcar escolhe o modo, e
    // desmarcar (o vizinho foi marcado) não faz nada.
    public bool IsDefaultAlerts
    {
        get => AlertMode == DeadlineAlertMode.Default;
        set => Choose(value, DeadlineAlertMode.Default);
    }

    public bool IsSilentAlerts
    {
        get => AlertMode == DeadlineAlertMode.Silent;
        set => Choose(value, DeadlineAlertMode.Silent);
    }

    public bool IsDayBeforeAlerts
    {
        get => AlertMode == DeadlineAlertMode.DayBefore;
        set => Choose(value, DeadlineAlertMode.DayBefore);
    }

    public bool IsCustomAlerts
    {
        get => AlertMode == DeadlineAlertMode.Custom;
        set => Choose(value, DeadlineAlertMode.Custom);
    }

    private void Choose(bool chosen, DeadlineAlertMode mode)
    {
        if (chosen)
        {
            AlertMode = mode;
        }
    }

    /// <summary>
    /// Próxima ação e estimativa aparecem quando a tarefa é longa — tem prazo —
    /// ou quando já foram preenchidas. A tarefa de cinco minutos não ganha
    /// formulário (§14: opcional, e não obrigatório).
    /// </summary>
    public bool ShowsPlan => HasDeadline || !string.IsNullOrWhiteSpace(NextAction) || EstimateHours is not null;

    public bool HasPlanChanges =>
        Normalize(NextAction) != _persistedNextAction || ToEstimate(EstimateHours) != _persistedEstimate;

    public void Load(TaskRowViewModel row)
    {
        _loading = true;

        try
        {
            _taskId = row.TaskId;
            _occurrenceId = row.OccurrenceId;
            _persistedNextAction = row.NextAction;
            _persistedEstimate = row.Estimate;

            View = row.Deadline;
            IsReadOnly = row.IsCompleted;
            IsEditing = false;
            ErrorMessage = null;
            NextAction = row.NextAction ?? string.Empty;
            EstimateHours = row.Estimate is { } estimate ? (decimal)estimate.TotalHours : null;
            ShowAlerts(row.DeadlineAlerts);
            ResetCustom();
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(HasPlanChanges));
    }

    /// <summary>"Personalizado…" do menu da linha: a janela abre já escolhendo.</summary>
    public void BeginEdit()
    {
        if (IsEditable)
        {
            IsEditing = true;
        }
    }

    [RelayCommand]
    private void ToggleEditing() => IsEditing = !IsEditing && IsEditable;

    /// <param name="shortcut">O nome do atalho, vindo do <c>CommandParameter</c> do botão.</param>
    [RelayCommand]
    private Task UseShortcutAsync(string shortcut, CancellationToken cancellationToken) =>
        Enum.TryParse<DeadlineShortcut>(shortcut, out var parsed)
            ? SetAsync(new SetDeadlineShortcut(_occurrenceId, parsed), cancellationToken)
            : Task.CompletedTask;

    private bool CanApplyCustom() => CustomDate is not null && CustomTime is not null;

    [RelayCommand(CanExecute = nameof(CanApplyCustom))]
    private Task ApplyCustomAsync(CancellationToken cancellationToken) =>
        SetAsync(
            new SetDeadline(
                _occurrenceId,
                DateOnly.FromDateTime(CustomDate!.Value),
                TimeOnly.FromTimeSpan(CustomTime!.Value)),
            cancellationToken);

    [RelayCommand]
    private async Task ClearAsync(CancellationToken cancellationToken)
    {
        if (await TryAsync(
                () => runner.RunAsync<ClearDeadlineHandler>(
                    (handler, token) => handler.HandleAsync(new ClearDeadline(_occurrenceId), token),
                    cancellationToken),
                "Não foi possível remover o prazo."))
        {
            View = null;
            IsEditing = false;
            ResetCustom();
            Changed?.Invoke();
        }
    }

    private bool CanSavePlan() => IsEditable && HasPlanChanges;

    [RelayCommand(CanExecute = nameof(CanSavePlan))]
    private async Task SavePlanAsync(CancellationToken cancellationToken)
    {
        var nextAction = Normalize(NextAction);
        var estimate = ToEstimate(EstimateHours);

        if (await TryAsync(
                () => runner.RunAsync<UpdateTaskPlanHandler>(
                    (handler, token) => handler.HandleAsync(new UpdateTaskPlan(_taskId, nextAction, estimate), token),
                    cancellationToken),
                "Não foi possível salvar a próxima ação."))
        {
            _persistedNextAction = nextAction;
            _persistedEstimate = estimate;
            OnPropertyChanged(nameof(HasPlanChanges));
            SavePlanCommand.NotifyCanExecuteChanged();
            Changed?.Invoke();
        }
    }

    partial void OnAlertModeChanged(DeadlineAlertMode value)
    {
        if (value == DeadlineAlertMode.Custom && !_loading)
        {
            // Personalizado começa do que já valia, e não de tudo desligado.
            ShowStages(DeadlineAlertPolicy.Default.Stages);
        }

        SaveAlerts();
    }

    partial void OnAlertSevenDaysChanged(bool value) => SaveCustomAlerts();

    partial void OnAlertThreeDaysChanged(bool value) => SaveCustomAlerts();

    partial void OnAlertOneDayChanged(bool value) => SaveCustomAlerts();

    partial void OnAlertEightHoursChanged(bool value) => SaveCustomAlerts();

    partial void OnAlertTwoHoursChanged(bool value) => SaveCustomAlerts();

    partial void OnAlertOverdueChanged(bool value) => SaveCustomAlerts();

    private void SaveCustomAlerts()
    {
        if (IsCustomAlerts)
        {
            SaveAlerts();
        }
    }

    private void SaveAlerts()
    {
        if (_loading || IsReadOnly)
        {
            return;
        }

        DeadlineAlertStage? alerts = AlertMode switch
        {
            DeadlineAlertMode.Silent => DeadlineAlertStage.None,
            DeadlineAlertMode.DayBefore => DeadlineAlertStages.OnlyTheDayBefore,
            DeadlineAlertMode.Custom => SelectedStages(),
            _ => null,
        };

        _ = TryAsync(
            () => runner.RunAsync<SetTaskDeadlineAlertsHandler>(
                (handler, token) => handler.HandleAsync(new SetTaskDeadlineAlerts(_taskId, alerts), token),
                CancellationToken.None),
            "Não foi possível salvar os avisos desta tarefa.");
    }

    private async Task SetAsync<TCommand>(TCommand command, CancellationToken cancellationToken)
        where TCommand : notnull
    {
        TaskDeadlineView? view = null;

        var saved = await TryAsync(
            async () => view = await runner.RunAsync<SetDeadlineHandler, TaskDeadlineView>(
                (handler, token) => command switch
                {
                    SetDeadline custom => handler.HandleAsync(custom, token),
                    SetDeadlineShortcut shortcut => handler.HandleAsync(shortcut, token),
                    _ => throw new ArgumentException("Pedido de prazo desconhecido.", nameof(command)),
                },
                cancellationToken),
            "Não foi possível definir o prazo.");

        if (saved)
        {
            View = view;
            IsEditing = false;
            ResetCustom();
            Changed?.Invoke();
        }
    }

    private void ShowAlerts(DeadlineAlertStage? alerts)
    {
        AlertMode = alerts switch
        {
            null => DeadlineAlertMode.Default,
            DeadlineAlertStage.None => DeadlineAlertMode.Silent,
            DeadlineAlertStages.OnlyTheDayBefore => DeadlineAlertMode.DayBefore,
            _ => DeadlineAlertMode.Custom,
        };

        ShowStages(alerts is { } custom and not DeadlineAlertStage.None ? custom : DeadlineAlertPolicy.Default.Stages);
    }

    /// <summary>Mostra os degraus sem gravar a cada caixa marcada.</summary>
    private void ShowStages(DeadlineAlertStage stages)
    {
        var wasLoading = _loading;
        _loading = true;

        try
        {
            AlertSevenDays = stages.HasFlag(DeadlineAlertStage.SevenDays);
            AlertThreeDays = stages.HasFlag(DeadlineAlertStage.ThreeDays);
            AlertOneDay = stages.HasFlag(DeadlineAlertStage.OneDay);
            AlertEightHours = stages.HasFlag(DeadlineAlertStage.EightHours);
            AlertTwoHours = stages.HasFlag(DeadlineAlertStage.TwoHours);
            AlertOverdue = stages.HasFlag(DeadlineAlertStage.Overdue);
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private DeadlineAlertStage SelectedStages() =>
        (AlertSevenDays ? DeadlineAlertStage.SevenDays : 0)
        | (AlertThreeDays ? DeadlineAlertStage.ThreeDays : 0)
        | (AlertOneDay ? DeadlineAlertStage.OneDay : 0)
        | (AlertEightHours ? DeadlineAlertStage.EightHours : 0)
        | (AlertTwoHours ? DeadlineAlertStage.TwoHours : 0)
        | (AlertOverdue ? DeadlineAlertStage.Overdue : 0);

    /// <summary>O "personalizado" abre no prazo atual, ou vazio para escolher.</summary>
    private void ResetCustom()
    {
        CustomDate = View?.Deadline.Date.ToDateTime(TimeOnly.MinValue);
        CustomTime = View?.Deadline.Time.ToTimeSpan();
    }

    private static string? Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static TimeSpan? ToEstimate(decimal? hours) =>
        hours is { } value && value > 0 ? TimeSpan.FromHours((double)value) : null;

    /// <summary>Mesmo tratamento das outras telas (ADR-008): a regra de negócio fala, o resto vai para o log.</summary>
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
            logger.LogError(exception, "TaskDeadlineOperationFailed");
            ErrorMessage = fallbackMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
