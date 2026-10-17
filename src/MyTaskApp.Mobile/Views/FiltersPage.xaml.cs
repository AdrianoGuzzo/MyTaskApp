using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

/// <summary>A folha de filtros: o mesmo ViewModel da lista, que já preparou as opções ao abrir.</summary>
public partial class FiltersPage
{
    public FiltersPage(TaskListViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
