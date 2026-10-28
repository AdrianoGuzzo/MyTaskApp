using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

/// <summary>Recebe a conversa pela rota (<c>session?id=…&amp;device=…</c>) e só lê enquanto aparece.</summary>
public partial class RemoteSessionPage : IQueryAttributable
{
    private readonly RemoteSessionViewModel _viewModel;

    private Guid? _sessionId;

    private Guid _deviceId;

    public RemoteSessionPage(RemoteSessionViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var value) && Guid.TryParse(value?.ToString(), out var id))
        {
            _sessionId = id;
        }

        if (query.TryGetValue("device", out var device) && Guid.TryParse(device?.ToString(), out var deviceId))
        {
            _deviceId = deviceId;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_sessionId is { } id)
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
