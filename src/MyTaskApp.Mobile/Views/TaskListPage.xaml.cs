using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

public partial class TaskListPage
{
    private const string ConnectOfferedKey = "connect-offered";

    private readonly TaskListViewModel _viewModel;

    private readonly MobileLifecycle _lifecycle;

    private bool _loaded;

    public TaskListPage(TaskListViewModel viewModel, MobileLifecycle lifecycle)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _lifecycle = lifecycle;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        try
        {
            // O banco nasce pelas migrations na primeira abertura; a lista espera.
            await _lifecycle.StartAsync();
        }
        catch (Exception)
        {
            await _viewModel.LoadAsync();
            return;
        }

        if (!_loaded)
        {
            _loaded = true;
            await _viewModel.LoadAsync();
            await _viewModel.LoadSyncAsync();
            await OfferConnectionOnceAsync();
        }
        else
        {
            await _viewModel.LoadSyncAsync();
        }
    }

    /// <summary>Na primeira vez, sem conta e com servidor, sugere entrar; depois, fica na aba Conta.</summary>
    private async Task OfferConnectionOnceAsync()
    {
        if (_viewModel.Sync?.Kind == SyncIndicatorKind.LocalOnly && !Preferences.Default.Get(ConnectOfferedKey, false))
        {
            Preferences.Default.Set(ConnectOfferedKey, true);
            await Shell.Current.GoToAsync(Adapters.ShellNavigator.Connect);
        }
    }
}
