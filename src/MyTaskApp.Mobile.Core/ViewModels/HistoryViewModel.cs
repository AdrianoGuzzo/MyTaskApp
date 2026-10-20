using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Presentation;

namespace MyTaskApp.Mobile.Core.ViewModels;

/// <summary>
/// A aba Histórico (ADR-076): o que já saiu da frente — concluídas e
/// canceladas —, com a mesma busca, os mesmos filtros e o mesmo dispositivo da
/// aba Tarefas. Reabrir daqui devolve a tarefa para lá.
/// </summary>
public sealed class HistoryViewModel(
    IUseCaseRunner runner,
    OriginCatalog origins,
    DeviceFocus focus,
    ISyncAccount account,
    SyncScheduler scheduler,
    IDataChangeNotifier notifier,
    DevicePlacementState placements,
    IUiDispatcher dispatcher,
    INavigator navigator,
    IDialogService dialogs,
    TimeProvider time,
    ILogger<HistoryViewModel> logger)
    : TaskListViewModelBase(
        [new(TaskListPreset.Completed, "Concluídas"), new(TaskListPreset.Cancelled, "Canceladas")],
        runner,
        origins,
        focus,
        account,
        scheduler,
        notifier,
        placements,
        dispatcher,
        navigator,
        dialogs,
        time,
        logger)
{
    public override bool IsHistory => true;

    public override string TabTitle => "Histórico";

    public override string HeaderCaption => "Histórico";

    public override string EmptyTitle => "Nada no histórico";

    public override string EmptyMessage => "O que você concluir ou cancelar aparece aqui.";
}
