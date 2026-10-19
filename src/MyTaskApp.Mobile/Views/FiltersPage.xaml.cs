using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

/// <summary>
/// A folha de filtros: o mesmo ViewModel da aba que a abriu, que já preparou
/// as opções — a da lista de Tarefas, ou a do Histórico (<c>filters?for=history</c>).
/// </summary>
public partial class FiltersPage : IQueryAttributable
{
    private readonly HistoryViewModel _history;

    public FiltersPage(TaskListViewModel tasks, HistoryViewModel history)
    {
        InitializeComponent();
        BindingContext = tasks;
        _history = history;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("for", out var value) && value?.ToString() == "history")
        {
            BindingContext = _history;
        }
    }
}
