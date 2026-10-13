using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MyTaskApp.Application.Devices;
using MyTaskApp.Domain.Devices;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma escolha do seletor ou da caixa de captura: o texto e o que ela quer dizer.</summary>
/// <param name="Placement">Para onde vai o que é criado; <c>null</c> = este computador (o padrão).</param>
public sealed record DeviceOption(string Label, DeviceView View, DevicePlacement? Placement = null)
{
    public override string ToString() => Label;
}

/// <summary>
/// "De qual computador" a lista mostra, e para onde vai o que se escreve na
/// caixa (ADR-062). Só aparece quando a conta tem outro computador: sem isso
/// não há o que separar, e o app fica igual ao de sempre.
/// </summary>
/// <remarks>
/// A escolha não é lembrada entre aberturas: o app sempre abre neste
/// computador, que é a pergunta do dia a dia. Espiar o outro é exceção.
/// </remarks>
public sealed partial class DeviceViewSelectorViewModel : ObservableObject
{
    public const string ThisComputer = "Este computador";

    public const string AllComputers = "Todos os computadores";

    private readonly DevicePlacementState? _state;

    public DeviceViewSelectorViewModel(DevicePlacementState? state)
    {
        _state = state;
        Refresh();
    }

    /// <summary>Este computador, cada outro, e todos.</summary>
    public ObservableCollection<DeviceOption> Views { get; } = [];

    /// <summary>Para onde vai o que a caixa de captura cria: aqui, todos, ou outro computador.</summary>
    public ObservableCollection<DeviceOption> Destinations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(View), nameof(IsShowingOthers))]
    private DeviceOption? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSendingAway))]
    private DeviceOption? _captureTarget;

    /// <summary>A conta tem outro computador: o seletor e o destino da captura aparecem.</summary>
    [ObservableProperty]
    private bool _isAvailable;

    /// <summary>A visão escolhida; antes de haver escolha, este computador.</summary>
    public DeviceView View => Selected?.View ?? DeviceView.Here;

    /// <summary>A lista mostra mais do que este computador: as linhas ganham o selo de onde estão.</summary>
    public bool IsShowingOthers => View.Kind != DeviceViewKind.Here;

    /// <summary>A captura vai criar em outro lugar que não só aqui: a caixa avisa.</summary>
    public bool IsSendingAway => CaptureTarget?.Placement is not null;

    /// <summary>A visão mudou: quem mostra a lista recarrega.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>
    /// Remonta as opções a partir dos computadores da conta, mantendo a escolha.
    /// Só mexe nas coleções quando algo mudou, para o seletor não piscar a
    /// cada recarga da lista.
    /// </summary>
    public void Refresh()
    {
        var others = _state?.OtherDevices ?? [];
        IsAvailable = _state is { HasOtherComputers: true };

        List<DeviceOption> views = IsAvailable
            ? [
                new(ThisComputer, DeviceView.Here),
                .. others.Select(device => new DeviceOption(device.Nickname, DeviceView.Of(device.DeviceId))),
                new(AllComputers, DeviceView.All),
            ]
            : [new(ThisComputer, DeviceView.Here)];

        List<DeviceOption> destinations = IsAvailable
            ? [
                new(ThisComputer, DeviceView.Here),
                new(AllComputers, DeviceView.All, DevicePlacement.Everywhere),
                .. others.Select(device => new DeviceOption(
                    $"Enviar para {device.Nickname}",
                    DeviceView.Of(device.DeviceId),
                    DevicePlacement.On(device.DeviceId))),
            ]
            : [new(ThisComputer, DeviceView.Here)];

        var view = View;
        var target = CaptureTarget?.View;

        Replace(Views, views);
        Replace(Destinations, destinations);

        // Um computador revogado some da lista: a escolha volta para este.
        Selected = Views.FirstOrDefault(option => option.View == view) ?? Views[0];
        CaptureTarget = Destinations.FirstOrDefault(option => option.View == target) ?? Destinations[0];
    }

    /// <summary>Volta a captura para este computador: mandar para o outro é exceção, e não o padrão seguinte.</summary>
    public void ResetCaptureTarget() => CaptureTarget = Destinations[0];

    partial void OnSelectedChanged(DeviceOption? oldValue, DeviceOption? newValue)
    {
        if (oldValue is not null && newValue is not null && oldValue.View != newValue.View)
        {
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void Replace(ObservableCollection<DeviceOption> target, List<DeviceOption> options)
    {
        if (target.SequenceEqual(options))
        {
            return;
        }

        target.Clear();

        foreach (var option in options)
        {
            target.Add(option);
        }
    }
}
