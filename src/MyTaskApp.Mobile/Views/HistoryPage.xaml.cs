using MyTaskApp.Mobile.Adapters;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

/// <summary>A aba Histórico: concluídas e canceladas. Carrega na primeira visita; depois, os avisos de mudança mantêm em dia.</summary>
public partial class HistoryPage
{
    private readonly HistoryViewModel _viewModel;

    private readonly MobileLifecycle _lifecycle;

    private bool _loaded;

    public HistoryPage(HistoryViewModel viewModel, MobileLifecycle lifecycle, MauiThemeService themes)
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
        }

        await _viewModel.LoadSyncAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.DismissUndo();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _viewModel.RepaintSync();

        if (_loaded)
        {
            _ = _viewModel.LoadAsync();
        }
    }
}
