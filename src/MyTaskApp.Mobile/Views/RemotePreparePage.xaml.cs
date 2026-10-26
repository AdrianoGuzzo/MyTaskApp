using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

/// <summary>Recebe a tarefa pela rota (<c>prepare?id=…</c>); carrega uma vez, para voltar da operação sem perder o formulário.</summary>
public partial class RemotePreparePage : IQueryAttributable
{
    private readonly RemotePrepareViewModel _viewModel;

    private Guid? _taskId;

    private bool _loaded;

    public RemotePreparePage(RemotePrepareViewModel viewModel)
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

        if (!_loaded && _taskId is { } id)
        {
            _loaded = true;
            await _viewModel.LoadAsync(id);
        }
    }
}
