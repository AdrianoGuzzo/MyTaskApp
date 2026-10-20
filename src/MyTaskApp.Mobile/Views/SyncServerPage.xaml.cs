using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

public partial class SyncServerPage
{
    private readonly SyncServerViewModel _viewModel;

    private readonly MobileLifecycle _lifecycle;

    public SyncServerPage(SyncServerViewModel viewModel, MobileLifecycle lifecycle)
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
            // O servidor salvo só vale depois da abertura do banco.
            await _lifecycle.StartAsync();
        }
        catch (Exception)
        {
            return;
        }

        await _viewModel.LoadAsync();
    }
}
