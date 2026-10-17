using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

/// <summary>Recebe o id pela rota (<c>task?id=…</c>) e relê ao voltar da edição.</summary>
public partial class TaskDetailPage : IQueryAttributable
{
    private readonly TaskDetailViewModel _viewModel;

    private Guid? _taskId;

    public TaskDetailPage(TaskDetailViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var value) && Guid.TryParse(value?.ToString(), out var id))
        {
            _taskId = id;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_taskId is { } id)
        {
            await _viewModel.LoadAsync(id);
        }
    }
}
