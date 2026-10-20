using System.ComponentModel;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

/// <summary>
/// A lista de uma aba; herda o ViewModel da página que a hospeda. Aqui ficam
/// só as animações da tela — a barra "Desfazer" e a bolinha da sincronização —
/// e o foco da busca.
/// </summary>
public partial class TaskListView
{
    private const string PulseAnimation = "sync-pulse";

    private TaskListViewModelBase? _viewModel;

    public TaskListView()
    {
        InitializeComponent();
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = BindingContext as TaskListViewModelBase;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
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
        if (_viewModel is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(TaskListViewModelBase.HasUndo):
                _ = ShowUndoAsync(_viewModel.HasUndo);
                break;

            case nameof(TaskListViewModelBase.Sync):
                Pulse(_viewModel.Sync?.Kind == SyncIndicatorKind.Syncing);
                break;

            // A lupa abre a busca já com o teclado; depois do layout, quando a barra já está visível.
            case nameof(TaskListViewModelBase.IsSearchOpen) when _viewModel.IsSearchOpen:
                Dispatcher.Dispatch(() => Search.Focus());
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
            SemanticScreenReader.Default.Announce(_viewModel?.UndoMessage + " Desfazer disponível.");
            await Task.WhenAll(UndoBar.FadeToAsync(1, 180, Easing.CubicOut), UndoBar.TranslateToAsync(0, 0, 180, Easing.CubicOut));
        }
        else
        {
            await Task.WhenAll(UndoBar.FadeToAsync(0, 150, Easing.CubicIn), UndoBar.TranslateToAsync(0, 24, 150, Easing.CubicIn));

            if (_viewModel is not { HasUndo: true })
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
        }.Commit(SyncDot, PulseAnimation, length: 1200, repeat: () => _viewModel?.Sync?.Kind == SyncIndicatorKind.Syncing);
    }
}
