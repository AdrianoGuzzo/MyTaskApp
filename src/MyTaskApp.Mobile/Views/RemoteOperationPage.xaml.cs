using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

/// <summary>Recebe a operação pela rota (<c>operation?id=…&amp;device=…</c>) e só lê enquanto aparece.</summary>
public partial class RemoteOperationPage : IQueryAttributable
{
    private readonly RemoteOperationViewModel _viewModel;

    private Guid? _operationId;

    private Guid _deviceId;

    public RemoteOperationPage(RemoteOperationViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var value) && Guid.TryParse(value?.ToString(), out var id))
        {
            _operationId = id;
        }

        if (query.TryGetValue("device", out var device) && Guid.TryParse(device?.ToString(), out var deviceId))
        {
            _deviceId = deviceId;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_operationId is { } id)
        {
            await _viewModel.OpenAsync(id, _deviceId);
        }
    }

    protected override void OnDisappearing()
    {
        _viewModel.Close();
        base.OnDisappearing();
    }
}
