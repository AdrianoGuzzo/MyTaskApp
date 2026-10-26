using System.ComponentModel;
using MyTaskApp.Mobile.Adapters;
using MyTaskApp.Mobile.Core.ViewModels;
using MyTaskApp.Mobile.Views;

namespace MyTaskApp.Mobile;

/// <summary>
/// Quatro abas, Tarefas, Histórico, Remoto e Conta; o resto entra por cima
/// delas. A aba Remoto diz quantas aprovações esperam, de qualquer tela.
/// </summary>
public partial class AppShell
{
    private readonly RemoteViewModel _remote;

    public AppShell(RemoteViewModel remote)
    {
        InitializeComponent();

        _remote = remote;
        _remote.PropertyChanged += OnRemoteChanged;

        Routing.RegisterRoute(ShellNavigator.Task, typeof(TaskDetailPage));
        Routing.RegisterRoute(ShellNavigator.TaskForm, typeof(TaskFormPage));
        Routing.RegisterRoute(ShellNavigator.Filters, typeof(FiltersPage));
        Routing.RegisterRoute(ShellNavigator.Connect, typeof(ConnectPage));
        Routing.RegisterRoute(ShellNavigator.Server, typeof(SyncServerPage));
        Routing.RegisterRoute(ShellNavigator.Machine, typeof(RemoteMachinePage));
        Routing.RegisterRoute(ShellNavigator.Operation, typeof(RemoteOperationPage));
        Routing.RegisterRoute(ShellNavigator.Prepare, typeof(RemotePreparePage));
    }

    private void OnRemoteChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteViewModel.TabTitle))
        {
            MainThread.BeginInvokeOnMainThread(() => RemoteTab.Title = _remote.TabTitle);
        }
    }
}
