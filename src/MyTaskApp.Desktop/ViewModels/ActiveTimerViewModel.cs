using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// O relógio do cronômetro que corre (ADR-052): a linha, a faixa do HUD e a aba
/// "Tempo" leem daqui. É a única coisa no app que tica a cada segundo — e o
/// tique só redesenha: o tempo é sempre <c>agora − StartedAt</c>, e o banco só
/// muda no ▶ e no ⏹.
/// </summary>
/// <remarks>
/// Um só objeto para o app inteiro, e não um por linha: as linhas são
/// recriadas a cada recarga do quadro, e um relógio por linha recomeçaria do
/// zero a cada minuto. O timer só existe enquanto há cronômetro correndo.
/// </remarks>
public sealed partial class ActiveTimerViewModel(TimeProvider timeProvider) : ObservableObject, IDisposable
{
    public static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(1);

    private ITimer? _ticker;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(TaskTitle))]
    [NotifyPropertyChangedFor(nameof(OccurrenceId))]
    [NotifyPropertyChangedFor(nameof(Tip))]
    private ActiveTimerView? _current;

    [ObservableProperty]
    private TimeSpan _elapsed;

    [ObservableProperty]
    private string _elapsedText = WorkTimeFormatter.Clock(TimeSpan.Zero);

    /// <summary>O cronômetro trocou de tarefa, começou ou parou: a janela aberta recarrega a aba "Tempo".</summary>
    public event Action<ActiveTimerView?, ActiveTimerView?>? Changed;

    public bool IsRunning => Current is not null;

    public string TaskTitle => Current?.TaskTitle ?? string.Empty;

    public Guid? OccurrenceId => Current?.OccurrenceId;

    public string Tip => Current is null ? string.Empty : $"Trabalhando em \"{Current.TaskTitle}\" — clique em ⏹ para parar.";

    /// <summary>O que o banco disse por último. Chamado a cada recarga do quadro, e depois de ▶ e ⏹.</summary>
    public void Show(ActiveTimerView? timer)
    {
        var previous = Current;

        Current = timer;
        Tick();

        if (timer is null)
        {
            _ticker?.Dispose();
            _ticker = null;
        }
        else
        {
            _ticker ??= timeProvider.CreateTimer(
                _ => Dispatcher.UIThread.Post(Tick),
                state: null,
                dueTime: TickEvery,
                period: TickEvery);
        }

        if (previous?.EntryId != timer?.EntryId)
        {
            Changed?.Invoke(previous, timer);
        }
    }

    /// <summary>Recalcula o relógio a partir do início gravado. Não conta nada, só subtrai.</summary>
    public void Tick()
    {
        Elapsed = Current is { } timer ? timeProvider.GetUtcNow() - timer.StartedAt : TimeSpan.Zero;
        ElapsedText = WorkTimeFormatter.Clock(Elapsed);
    }

    public void Dispose()
    {
        _ticker?.Dispose();
        _ticker = null;
    }
}
