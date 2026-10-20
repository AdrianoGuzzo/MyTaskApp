using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Presentation;

namespace MyTaskApp.Mobile.Core.ViewModels;

/// <summary>
/// A aba Tarefas, a tela principal do celular (ADR-076, ADR-079): só o que
/// falta fazer, sem atalhos no topo — "Só as vencidas" fica na folha de
/// filtros. Concluídas e canceladas ficam no <see cref="HistoryViewModel"/>.
/// </summary>
public sealed class TaskListViewModel(
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
    ILogger<TaskListViewModel> logger)
    : TaskListViewModelBase(
        [new(TaskListPreset.Pending, "Pendentes")],
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
    public override bool IsHistory => false;

    public override string TabTitle => "Tarefas";

    public override string HeaderCaption => "Pendentes";

    public override string EmptyTitle => "Nada pendente";

    public override string EmptyMessage =>
        "Crie uma tarefa em Nova tarefa, ou entre com sua conta para trazer as tarefas dos outros dispositivos. As concluídas ficam no Histórico.";

    public override bool OffersSignInWhenEmpty => true;
}
