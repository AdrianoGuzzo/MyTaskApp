using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A aba "Tempo" da tarefa (ADR-052): o cronômetro, o total contra a estimativa
/// e o histórico por dia, com lançar, corrigir e excluir. Não decide nada — as
/// regras (um cronômetro no app, sem sobreposição, fim depois do início) são da
/// Application; aqui só se pede e se mostra.
/// </summary>
/// <remarks>
/// O relógio que corre é o <see cref="ActiveTimerViewModel"/> do app, o mesmo
/// da linha: os dois mostram o mesmo segundo. A janela assina as mudanças dele
/// enquanto está aberta, e <see cref="Detach"/> solta a assinatura ao fechar —
/// o relógio é singleton e seguraria a aba viva.
/// </remarks>
public sealed partial class TaskTimeLogViewModel : ObservableObject
{
    private readonly IUseCaseRunner _runner;
    private readonly ITimeEntryEditor _editor;
    private readonly IConfirmationDialog _confirmation;
    private readonly IUserClock _clock;
    private readonly ILogger<TaskTimeLogViewModel> _logger;

    private Guid _occurrenceId;
    private string _title = string.Empty;
    private bool _isCompleted;

    public TaskTimeLogViewModel(
        IUseCaseRunner runner,
        ITimeEntryEditor editor,
        IConfirmationDialog confirmation,
        ActiveTimerViewModel activeTimer,
        IUserClock clock,
        ILogger<TaskTimeLogViewModel> logger)
    {
        _runner = runner;
        _editor = editor;
        _confirmation = confirmation;
        _clock = clock;
        _logger = logger;
        Timer = activeTimer;
        Timer.PropertyChanged += OnTimerChanged;
    }

    /// <summary>O relógio do app: a aba mostra o mesmo segundo que a linha.</summary>
    public ActiveTimerViewModel Timer { get; }

    /// <summary>Do dia mais recente ao mais antigo.</summary>
    public ObservableCollection<TimeEntryDayViewModel> Days { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(EstimateText))]
    [NotifyPropertyChangedFor(nameof(HasEstimate))]
    [NotifyPropertyChangedFor(nameof(IsOverEstimate))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private TaskTimeLogView? _log;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>A aba mudou o tempo da tarefa: a lista precisa recarregar o total e o relógio da linha.</summary>
    public event Action? Changed;

    public Guid OccurrenceId => _occurrenceId;

    /// <summary>O cronômetro do app corre nesta tarefa.</summary>
    public bool IsRunningHere => Timer.OccurrenceId == _occurrenceId && _occurrenceId != Guid.Empty;

    /// <summary>▶ só em trabalho aberto; ⏹ sempre que corre aqui.</summary>
    public bool CanToggleTimer => IsRunningHere || !_isCompleted;

    public string TimerGlyph => IsRunningHere ? TimerGlyphs.Stop : TimerGlyphs.Play;

    public string TimerLabel => IsRunningHere ? "Parar" : "Iniciar";

    public bool IsEmpty => Log is { IsEmpty: true };

    /// <summary>"Registrado 4h 32min · em andamento 27min" — o que corre fica à parte do total.</summary>
    public string SummaryText
    {
        get
        {
            var logged = WorkTimeFormatter.Duration(Log?.Logged ?? TimeSpan.Zero);

            return IsRunningHere
                ? $"Registrado {logged} · em andamento {WorkTimeFormatter.Duration(Timer.Elapsed)}"
                : $"Registrado {logged}";
        }
    }

    public bool HasEstimate => Log?.Estimate is not null;

    /// <summary>"4h 59min / 6h · 83%": o registrado mais o que corre, contra a estimativa.</summary>
    public string EstimateText => Log?.Estimate is { } estimate
        ? WorkTimeFormatter.AgainstEstimate(Spent, estimate)
        : string.Empty;

    /// <summary>Passou da estimativa: a linha ganha a cor de cautela, e nada é bloqueado.</summary>
    public bool IsOverEstimate => Log?.Estimate is { } estimate && Spent > estimate;

    private TimeSpan Spent => (Log?.Logged ?? TimeSpan.Zero) + (IsRunningHere ? Timer.Elapsed : TimeSpan.Zero);

    public void Load(TaskRowViewModel row)
    {
        _occurrenceId = row.OccurrenceId;
        _title = row.Title;
        _isCompleted = row.IsCompleted;
        Log = null;
        Days.Clear();
        ErrorMessage = null;
        NotifyTimerState();
    }

    /// <summary>A aba se atualiza a cada vez que aparece, e depois de cada mudança.</summary>
    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        if (_occurrenceId == Guid.Empty)
        {
            return;
        }

        TaskTimeLogView? log = null;

        var loaded = await TryAsync(
            async () => log = await _runner.RunAsync<GetTaskTimeLogHandler, TaskTimeLogView>(
                (handler, token) => handler.HandleAsync(new GetTaskTimeLog(_occurrenceId), token),
                cancellationToken),
            "Não foi possível carregar o tempo desta tarefa.");

        if (!loaded)
        {
            return;
        }

        Log = log;
        Days.Clear();

        foreach (var day in log!.Days)
        {
            Days.Add(new TimeEntryDayViewModel(day));
        }
    }

    /// <summary>▶ ou ⏹ na aba. A troca de tarefa pergunta, como na lista.</summary>
    [RelayCommand]
    public async Task ToggleTimerAsync(CancellationToken cancellationToken)
    {
        if (IsRunningHere)
        {
            var stopped = await TryAsync(
                () => _runner.RunAsync<StopTimerHandler>(
                    (handler, token) => handler.HandleAsync(new StopTimer(_occurrenceId), token),
                    cancellationToken),
                "Não foi possível parar o cronômetro.");

            if (stopped)
            {
                Timer.Show(null);
                await AfterChangeAsync(cancellationToken);
            }

            return;
        }

        if (!CanToggleTimer)
        {
            return;
        }

        ActiveTimerView? running = null;

        var read = await TryAsync(
            async () => running = await _runner.RunAsync<GetActiveTimerHandler, ActiveTimerView?>(
                (handler, token) => handler.HandleAsync(token),
                cancellationToken),
            "Não foi possível verificar o cronômetro.");

        if (!read)
        {
            return;
        }

        var replace = false;

        if (running is not null && running.OccurrenceId != _occurrenceId)
        {
            if (!await _confirmation.AskAsync(TodayViewModel.SwitchTimerPrompt(running.TaskTitle, _title)))
            {
                return;
            }

            replace = true;
        }

        ActiveTimerView? started = null;

        var ok = await TryAsync(
            async () => started = await _runner.RunAsync<StartTimerHandler, ActiveTimerView>(
                (handler, token) => handler.HandleAsync(new StartTimer(_occurrenceId, replace), token),
                cancellationToken),
            "Não foi possível iniciar o cronômetro.");

        if (ok)
        {
            Timer.Show(started);
            await AfterChangeAsync(cancellationToken);
        }
    }

    /// <summary>"+ Adicionar tempo": o período que o cronômetro não pegou.</summary>
    [RelayCommand]
    public async Task AddEntryAsync(CancellationToken cancellationToken)
    {
        var end = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Floor(_clock.CurrentTime.ToTimeSpan().TotalMinutes)));
        var start = end.Hour == 0 ? TimeOnly.MinValue : end.AddHours(-1);

        var saved = await _editor.EditAsync(new TimeEntryEditorRequest(
            "Adicionar tempo",
            "Adicionar",
            new TimeEntryDraft(_clock.Today, start, _clock.Today, end, null),
            _clock.Today,
            (draft, token) => SaveAsync(
                () => _runner.RunAsync<AddTimeEntryHandler, Guid>(
                    (handler, inner) => handler.HandleAsync(
                        new AddTimeEntry(_occurrenceId, draft.StartDate, draft.StartTime, draft.EndDate, draft.EndTime, draft.Note),
                        inner),
                    token))));

        if (saved)
        {
            await AfterChangeAsync(cancellationToken);
        }
    }

    [RelayCommand]
    public async Task EditEntryAsync(TimeEntryRowViewModel row, CancellationToken cancellationToken)
    {
        if (!row.CanEdit)
        {
            return;
        }

        var view = row.View;

        var saved = await _editor.EditAsync(new TimeEntryEditorRequest(
            "Editar período",
            "Salvar",
            new TimeEntryDraft(view.StartDate, view.StartTime, view.EndDate!.Value, view.EndTime!.Value, view.Note),
            _clock.Today,
            (draft, token) => SaveAsync(
                () => _runner.RunAsync<UpdateTimeEntryHandler>(
                    (handler, inner) => handler.HandleAsync(
                        new UpdateTimeEntry(view.Id, draft.StartDate, draft.StartTime, draft.EndDate, draft.EndTime, draft.Note),
                        inner),
                    token))));

        if (saved)
        {
            await AfterChangeAsync(cancellationToken);
        }
    }

    [RelayCommand]
    public async Task DeleteEntryAsync(TimeEntryRowViewModel row, CancellationToken cancellationToken)
    {
        if (!await _confirmation.AskAsync(DeletePrompt(row.View)))
        {
            return;
        }

        var deleted = await TryAsync(
            () => _runner.RunAsync<DeleteTimeEntryHandler>(
                (handler, token) => handler.HandleAsync(new DeleteTimeEntry(row.View.Id), token),
                cancellationToken),
            "Não foi possível excluir este período.");

        if (!deleted)
        {
            return;
        }

        if (row.IsActive)
        {
            Timer.Show(null);
        }

        await AfterChangeAsync(cancellationToken);
    }

    /// <summary>
    /// "Excluir registro de tempo?" com o período e a duração, e o foco em
    /// Cancelar (<see cref="ConfirmWindow"/>): é a única operação da aba sem volta.
    /// </summary>
    public static ConfirmationRequest DeletePrompt(TimeEntryView view) =>
        new(
            "Excluir registro de tempo?",
            $"{view.RangeLabel}{Environment.NewLine}{view.DurationLabel}",
            "Excluir",
            IsIrreversible: true);

    /// <summary>Solta o relógio do app. Chamado quando a janela fecha.</summary>
    public void Detach() => Timer.PropertyChanged -= OnTimerChanged;

    private async Task AfterChangeAsync(CancellationToken cancellationToken)
    {
        await ActivateAsync(cancellationToken);
        Changed?.Invoke();
    }

    /// <summary>A gravação do diálogo: a mensagem do domínio volta para ele, que fica aberto.</summary>
    private async Task<string?> SaveAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (DomainException exception)
        {
            return exception.Message;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "TimeEntrySaveFailed {OccurrenceId}", _occurrenceId);
            return "Não foi possível salvar este período.";
        }
    }

    private void OnTimerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ActiveTimerViewModel.Current) or nameof(ActiveTimerViewModel.Elapsed))
        {
            NotifyTimerState();
        }
    }

    private void NotifyTimerState()
    {
        OnPropertyChanged(nameof(IsRunningHere));
        OnPropertyChanged(nameof(CanToggleTimer));
        OnPropertyChanged(nameof(TimerGlyph));
        OnPropertyChanged(nameof(TimerLabel));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(EstimateText));
        OnPropertyChanged(nameof(IsOverEstimate));
    }

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
            _logger.LogError(exception, "TaskTimeLogOperationFailed {OccurrenceId}", _occurrenceId);
            ErrorMessage = fallbackMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>"Hoje · 2h 12min", com os períodos daquele dia.</summary>
public sealed class TimeEntryDayViewModel(TimeEntryDayView day)
{
    public string Label { get; } = day.Label;

    public string TotalText { get; } = WorkTimeFormatter.Duration(day.Total);

    public IReadOnlyList<TimeEntryRowViewModel> Entries { get; } =
        [.. day.Entries.Select(entry => new TimeEntryRowViewModel(entry))];
}

/// <summary>"14:00 → 15:30 · 1h 30min · Manual", e a observação embaixo.</summary>
public sealed class TimeEntryRowViewModel(TimeEntryView view)
{
    public TimeEntryView View { get; } = view;

    public string RangeLabel => View.RangeLabel;

    /// <summary>O que corre não tem duração fechada: o relógio fica no cabeçalho da aba.</summary>
    public string DurationLabel => View.IsActive ? "em andamento" : View.DurationLabel;

    public string SourceLabel => View.SourceLabel;

    public string? Note => View.Note;

    public bool HasNote => !string.IsNullOrWhiteSpace(View.Note);

    public bool IsActive => View.IsActive;

    /// <summary>O que corre se para antes de corrigir; excluir vale para os dois.</summary>
    public bool CanEdit => !View.IsActive;

    public string EditGlyph => "";

    public string DeleteGlyph => "";
}
