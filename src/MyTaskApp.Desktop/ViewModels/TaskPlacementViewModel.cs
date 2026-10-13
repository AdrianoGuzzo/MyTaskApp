using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Devices;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Devices;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Um lugar oferecido no menu "Onde aparece" (ADR-062).</summary>
public sealed record PlacementOption(string Label, DevicePlacement Placement, bool IsCurrent);

/// <summary>As opções do menu "Onde aparece", iguais na linha e na janela da tarefa.</summary>
public static class PlacementOptions
{
    /// <summary>
    /// Este computador, todos, e cada outro computador ativo da conta, com o
    /// lugar atual marcado. Sem lugar (legado) conta como "este computador":
    /// é onde ele ficará quando o assentamento passar.
    /// </summary>
    public static IReadOnlyList<PlacementOption> For(DevicePlacementState? placements, DevicePlacement? current)
    {
        if (placements is not { IsActive: true, Here: { } here })
        {
            return [];
        }

        var thisComputer = DevicePlacement.On(here);

        bool Is(DevicePlacement candidate) => current == candidate || (current is null && candidate == thisComputer);

        return
        [
            new("Só neste computador", thisComputer, Is(thisComputer)),
            new("Em todos os computadores", DevicePlacement.Everywhere, Is(DevicePlacement.Everywhere)),
            .. placements.OtherDevices.Select(device => new PlacementOption(
                $"Enviar para {device.Nickname}",
                DevicePlacement.On(device.DeviceId),
                Is(DevicePlacement.On(device.DeviceId)))),
        ];
    }

    /// <summary>"Aparece só neste computador", "Aparece em todos os computadores", "Está em Notebook".</summary>
    public static string? Describe(DevicePlacementState? placements, DevicePlacement? placement)
    {
        if (placements is not { HasOtherComputers: true })
        {
            return null;
        }

        return placement switch
        {
            null or { IsEverywhere: true } => "Aparece em todos os computadores",
            { DeviceId: var id } when placements.IsHere(id) => "Aparece só neste computador",
            _ => $"Está em {placements.Describe(placement)}: não aparece na lista daqui",
        };
    }
}

/// <summary>
/// O "onde aparece" na janela da tarefa (ADR-062): a frase abaixo da origem e o
/// botão que muda. Abrir uma tarefa que chegou de outro computador encerra o
/// destaque de chegada.
/// </summary>
public sealed partial class TaskPlacementViewModel(
    IUseCaseRunner runner,
    ILogger<TaskPlacementViewModel> logger,
    DevicePlacementState? placements = null) : ObservableObject
{
    private Guid _taskId;

    private DevicePlacement? _placement;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    private string? _text;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>A conta tem outro computador: a frase e o botão aparecem.</summary>
    public bool IsAvailable => Text is not null;

    public IReadOnlyList<PlacementOption> Options => PlacementOptions.For(placements, _placement);

    /// <summary>O lugar ou a chegada mudou: a lista redesenha.</summary>
    public event Action? Changed;

    public void Load(TaskRowViewModel row)
    {
        _taskId = row.TaskId;
        _placement = row.Placement;
        ErrorMessage = null;
        Text = PlacementOptions.Describe(placements, _placement);

        if (row.HasArrived)
        {
            _ = AcknowledgeArrivalAsync(row.TaskId);
        }
    }

    [RelayCommand]
    public async Task PlaceAsync(PlacementOption option, CancellationToken cancellationToken)
    {
        if (option.IsCurrent)
        {
            return;
        }

        ErrorMessage = null;

        try
        {
            await runner.RunAsync<PlaceTaskHandler, PlaceResult>(
                (handler, token) => handler.HandleAsync(new PlaceTask(_taskId, option.Placement), token),
                cancellationToken);

            _placement = option.Placement;
            Text = PlacementOptions.Describe(placements, _placement);
            Changed?.Invoke();
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "TaskPlacementFailed");
            ErrorMessage = "Não foi possível mudar onde a tarefa aparece.";
        }
    }

    private async Task AcknowledgeArrivalAsync(Guid taskId)
    {
        try
        {
            await runner.RunAsync<AcknowledgeArrivalHandler>(
                (handler, token) => handler.HandleAsync(new AcknowledgeArrival(taskId), token),
                CancellationToken.None);

            Changed?.Invoke();
        }
        catch (Exception exception)
        {
            // O destaque fica para a próxima abertura; nada mais depende disto.
            logger.LogWarning(exception, "TaskArrivalAcknowledgeFailed");
        }
    }
}
