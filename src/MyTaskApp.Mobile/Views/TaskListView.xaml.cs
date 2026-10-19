using System.ComponentModel;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

/// <summary>
/// A lista de uma aba; herda o ViewModel da página que a hospeda. Aqui ficam
/// só as animações da tela: a barra "Desfazer" e a bolinha da sincronização.
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

    /// <summary>
    /// O dispositivo lembrado pode ser o último chip, fora da tela ao abrir o
    /// app: a fileira rola até ele quando os chips ganham tamanho.
    /// </summary>
    /// <remarks>
    /// Depois do layout, pelo despachante, e por posição: no Android, rolar
    /// até o elemento durante o SizeChanged não faz nada.
    /// </remarks>
    private void OnDeviceChipsSized(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        var selected = DeviceChips.Children
            .OfType<VisualElement>()
            .FirstOrDefault(chip => chip.BindingContext is DeviceChip { IsSelected: true });

        if (selected is null || DeviceScroll.Width <= 0)
        {
            return;
        }

        var right = selected.X + selected.Width + DeviceChips.Padding.Right;

        if (right > DeviceScroll.ScrollX + DeviceScroll.Width)
        {
            await DeviceScroll.ScrollToAsync(right - DeviceScroll.Width, 0, animated: false);
        }
    });

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
