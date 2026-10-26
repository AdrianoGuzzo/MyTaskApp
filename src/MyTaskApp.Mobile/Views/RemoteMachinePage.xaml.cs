using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

/// <summary>Recebe a máquina pela rota (<c>machine?id=…</c>).</summary>
public partial class RemoteMachinePage : IQueryAttributable
{
    private readonly RemoteMachineViewModel _viewModel;

    private Guid? _deviceId;

    public RemoteMachinePage(RemoteMachineViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var value) && Guid.TryParse(value?.ToString(), out var id))
        {
            _deviceId = id;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_deviceId is { } id)
        {
            await _viewModel.LoadAsync(id);
        }
    }
}
