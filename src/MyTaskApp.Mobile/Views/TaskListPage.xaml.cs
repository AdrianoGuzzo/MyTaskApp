using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Adapters;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

/// <summary>A aba Tarefas: as pendentes. A lista em si é a <see cref="TaskListView"/>, a mesma do Histórico.</summary>
public partial class TaskListPage
{
    private const string ConnectOfferedKey = "connect-offered";

    private readonly TaskListViewModel _viewModel;

    private readonly MobileLifecycle _lifecycle;

    private bool _loaded;

    public TaskListPage(TaskListViewModel viewModel, MobileLifecycle lifecycle, MauiThemeService themes)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _lifecycle = lifecycle;
        themes.Changed += OnThemeChanged;
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

    /// <summary>Ao sair da lista, a barra "Desfazer" vai junto: ela fala da tela que se via.</summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.DismissUndo();
    }

    /// <summary>
    /// Tema novo: a bolinha é repintada e a lista relida; as linhas refeitas
    /// pegam as cores novas pelos conversores (prioridade, prazo).
    /// </summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _viewModel.RepaintSync();

        if (_loaded)
        {
            _ = _viewModel.LoadAsync();
        }
    }

    /// <summary>Na primeira vez, sem conta e com servidor, sugere entrar; depois, fica na aba Conta.</summary>
    private async Task OfferConnectionOnceAsync()
    {
        if (_viewModel.Sync?.Kind == SyncIndicatorKind.LocalOnly && !Preferences.Default.Get(ConnectOfferedKey, false))
        {
            Preferences.Default.Set(ConnectOfferedKey, true);
            await Shell.Current.GoToAsync(ShellNavigator.Connect);
        }
    }
}
