using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

public partial class ConnectPage
{
    public ConnectPage(ConnectViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
