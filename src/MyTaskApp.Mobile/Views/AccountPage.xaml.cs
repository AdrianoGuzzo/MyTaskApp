using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

public partial class AccountPage
{
    private readonly AccountViewModel _viewModel;

    private readonly MobileLifecycle _lifecycle;

    public AccountPage(AccountViewModel viewModel, MobileLifecycle lifecycle)
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
            await _lifecycle.StartAsync();
        }
        catch (Exception)
        {
            // A aba Tarefas mostra o erro; aqui, a conta só não carrega.
            return;
        }

        await _viewModel.LoadAsync();
    }
}
