using System.ComponentModel;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Adapters;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

public partial class TaskListPage
{
    private const string ConnectOfferedKey = "connect-offered";

    private const string PulseAnimation = "sync-pulse";

    private readonly TaskListViewModel _viewModel;

    private readonly MobileLifecycle _lifecycle;

    private bool _loaded;

    public TaskListPage(TaskListViewModel viewModel, MobileLifecycle lifecycle, MauiThemeService themes)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _lifecycle = lifecycle;
        _viewModel.PropertyChanged += OnViewModelChanged;
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

    /// <summary>"Buscar" no teclado: a busca já está correndo, então o teclado sai da frente.</summary>
    private async void OnSearchPressed(object? sender, EventArgs e)
    {
        if (sender is SearchBar search)
        {
            await search.HideSoftInputAsync(CancellationToken.None);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TaskListViewModel.HasUndo):
                _ = ShowUndoAsync(_viewModel.HasUndo);
                break;

            case nameof(TaskListViewModel.Sync):
                Pulse(_viewModel.Sync?.Kind == SyncIndicatorKind.Syncing);
                break;
        }
    }

    private async Task ShowUndoAsync(bool show)
    {
        UndoBar.AbortAnimation("FadeTo");
        UndoBar.AbortAnimation("TranslateTo");

        if (show)
        {
            UndoBar.TranslationY = 24;
            UndoBar.IsVisible = true;
            SemanticScreenReader.Default.Announce(_viewModel.UndoMessage + " Desfazer disponível.");
            await Task.WhenAll(UndoBar.FadeToAsync(1, 180, Easing.CubicOut), UndoBar.TranslateToAsync(0, 0, 180, Easing.CubicOut));
        }
        else
        {
            await Task.WhenAll(UndoBar.FadeToAsync(0, 150, Easing.CubicIn), UndoBar.TranslateToAsync(0, 24, 150, Easing.CubicIn));

            if (!_viewModel.HasUndo)
            {
                UndoBar.IsVisible = false;
            }
        }
    }

    /// <summary>Sincronizando, a bolinha pulsa como a do desktop; parada, fica acesa.</summary>
    private void Pulse(bool on)
    {
        if (!on)
        {
            SyncDot.AbortAnimation(PulseAnimation);
            SyncDot.Opacity = 1;
            return;
        }

        if (SyncDot.AnimationIsRunning(PulseAnimation))
        {
            return;
        }

        new Animation
        {
            { 0, 0.5, new Animation(value => SyncDot.Opacity = value, 1, 0.3) },
            { 0.5, 1, new Animation(value => SyncDot.Opacity = value, 0.3, 1) },
        }.Commit(SyncDot, PulseAnimation, length: 1200, repeat: () => _viewModel.Sync?.Kind == SyncIndicatorKind.Syncing);
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
