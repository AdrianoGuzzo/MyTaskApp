using MyTaskApp.Mobile.Adapters;
using MyTaskApp.Mobile.Views;

namespace MyTaskApp.Mobile;

/// <summary>Duas abas, Tarefas e Conta; o resto entra por cima delas.</summary>
public partial class AppShell
{
    public AppShell()
    {
        InitializeComponent();

        Routing.RegisterRoute(ShellNavigator.Task, typeof(TaskDetailPage));
        Routing.RegisterRoute(ShellNavigator.TaskForm, typeof(TaskFormPage));
        Routing.RegisterRoute(ShellNavigator.Filters, typeof(FiltersPage));
        Routing.RegisterRoute(ShellNavigator.Connect, typeof(ConnectPage));
    }
}
