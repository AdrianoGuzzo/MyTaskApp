using System.ComponentModel;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

/// <summary>
/// Recebe a sessão pela rota (<c>terminal?id=…&amp;device=…</c>) e liga só
/// enquanto aparece (ADR-101). Aqui fica só o que é da tela: o desenho, o
/// tamanho que cabe, a pinça, a rolagem e o teclado do sistema.
/// </summary>
public partial class RemoteTerminalPage : IQueryAttributable
{
    private readonly RemoteTerminalViewModel _viewModel;

    private readonly TerminalDrawable _drawable = new();

    private Guid? _sessionId;

    private Guid _deviceId;

    private double _pinchStartFont;

    private int _panLines;

    private bool _isActive;
    private bool _resetting;
    private bool _requestingControl;
    private bool _focusWhenGranted;
    private long _scrollbackAdded;

    public RemoteTerminalPage(RemoteTerminalViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        TerminalView.Drawable = _drawable;
        KeyboardEntry.Text = TerminalKeyboardEdit.Marker;

        KeyboardEntry.Keyboard = Keyboard.Create(KeyboardFlags.None);
        KeyboardEntry.Focused += (_, _) => KeyboardEntry.CursorPosition = KeyboardEntry.Text?.Length ?? 0;
        TerminalView.SizeChanged += (_, _) =>
        {
            UpdateScroll();
            _ = ResizeAsync();
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (_requestingControl) return;
            _requestingControl = true;
            try
            {
                if (_viewModel.CanTakeControl)
                {
                    _focusWhenGranted = true;
                    await _viewModel.TakeControlAsync();
                }
                else
                {
                    await OpenKeyboardAsync();
                }
            }
            finally { _requestingControl = false; }
        };
        TerminalTouchSurface.GestureRecognizers.Add(tap);

        var pinch = new PinchGestureRecognizer();
        pinch.PinchUpdated += OnPinch;
        TerminalTouchSurface.GestureRecognizers.Add(pinch);

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPan;
        TerminalTouchSurface.GestureRecognizers.Add(pan);
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
        _isActive = true;
        _viewModel.ScreenChanged += OnScreenChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;

        if (_sessionId is { } id)
        {
            await _viewModel.OpenAsync(_deviceId, id);
            await ResizeAsync();
        }
    }

    protected override async void OnDisappearing()
    {
        _isActive = false;
        _focusWhenGranted = false;
        KeyboardEntry.Unfocus();
        _viewModel.ScreenChanged -= OnScreenChanged;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        base.OnDisappearing();
        await _viewModel.CloseAsync();
    }

    private void OnScreenChanged()
    {
        var screen = _viewModel.Screen;
        var added = screen?.ScrollbackLinesAdded ?? 0;
        var sameScreen = ReferenceEquals(_drawable.Screen, screen);
        _drawable.Screen = screen;
        FitFont();
        _drawable.ScrollOffset = TerminalScroll.AfterOutput(
            _drawable.ScrollOffset, _drawable.MaximumOffset(TerminalView.Height), added - _scrollbackAdded, sameScreen);
        _scrollbackAdded = added;
        UpdateScroll();
    }

    private void UpdateScroll()
    {
        _drawable.ScrollOffset = Math.Clamp(_drawable.ScrollOffset, 0, _drawable.MaximumOffset(TerminalView.Height));
        ScrollEndButton.IsVisible = _drawable.ScrollOffset > 0;
        TerminalView.Invalidate();
    }

    private async void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteTerminalViewModel.FontSize))
        {
            FitFont();
            UpdateScroll();
            _ = ResizeAsync();
        }
        else if (e.PropertyName == nameof(RemoteTerminalViewModel.CanType))
        {
            _drawable.ShowCursor = _viewModel.CanType;
            if (_viewModel.CanType && _focusWhenGranted)
            {
                _focusWhenGranted = false;
                await OpenKeyboardAsync();
            }
            else if (!_viewModel.CanType)
            {
                KeyboardEntry.Unfocus();
                Reset();
            }
            TerminalView.Invalidate();
        }
    }

    /// <summary>
    /// A letra escolhida, ou menor se a tela de lá é mais larga que a daqui:
    /// quem dita o tamanho pode ser a janela de lá (ADR-098).
    /// </summary>
    private void FitFont()
    {
        var chosen = (float)_viewModel.FontSize;

        if (_drawable.Screen is { } screen && TerminalView.Width > 0 && _drawable.CellWidth > 0)
        {
            var ratio = _drawable.CellWidth / _drawable.FontSize;
            var fits = (float)(TerminalView.Width / (screen.Size.Columns * ratio));
            chosen = Math.Clamp(Math.Min(chosen, fits), (float)RemoteTerminalViewModel.MinFontSize, (float)RemoteTerminalViewModel.MaxFontSize);
        }

        _drawable.FontSize = chosen;
    }

    /// <summary>As colunas e linhas que cabem aqui, na letra escolhida; só valem com a vez (ADR-098).</summary>
    private Task ResizeAsync()
    {
        if (TerminalView.Width <= 0 || TerminalView.Height <= 0)
        {
            return Task.CompletedTask;
        }

        var ratio = _drawable.CellWidth / _drawable.FontSize;
        var heightRatio = _drawable.CellHeight / _drawable.FontSize;
        var font = _viewModel.FontSize;
        var size = new TerminalSize(
            Math.Max(20, (int)(TerminalView.Width / (font * ratio))),
            Math.Max(5, (int)(TerminalView.Height / (font * heightRatio))));

        return _viewModel.ResizeAsync(size);
    }

    private void OnPinch(object? sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
                _pinchStartFont = _viewModel.FontSize;
                break;
            case GestureStatus.Running:
                _viewModel.FontSize = Math.Clamp(
                    Math.Round(_viewModel.FontSize * e.Scale, 1),
                    RemoteTerminalViewModel.MinFontSize,
                    RemoteTerminalViewModel.MaxFontSize);
                break;
            case GestureStatus.Canceled:
                _viewModel.FontSize = _pinchStartFont;
                break;
        }
    }

    /// <summary>Arrastar para baixo mostra o histórico; para cima volta à tela de agora.</summary>
    private void OnPan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panLines = 0;
                break;
            case GestureStatus.Running:
                var lines = (int)(e.TotalY / Math.Max(1, _drawable.CellHeight));
                _drawable.ScrollOffset += lines - _panLines;
                _panLines = lines;
                UpdateScroll();
                break;
        }
    }

    private async Task OpenKeyboardAsync()
    {
        if (!_isActive || !_viewModel.CanType)
        {
            return;
        }

        KeyboardEntry.Focus();
        if (OperatingSystem.IsIOS())
        {
            await KeyboardEntry.ShowSoftInputAsync(CancellationToken.None);
        }
    }

    private void OnKeyboardTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_resetting || !_viewModel.CanType)
        {
            return;
        }

        OnScrollEndClicked(this, EventArgs.Empty);
        _ = _viewModel.ApplyKeyboardEditAsync(e.OldTextValue, e.NewTextValue);
        if (string.IsNullOrEmpty(e.NewTextValue))
        {
            Reset();
        }
    }

    private async void OnTerminalKeyClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: TerminalKeyButton button })
        {
            Reset();
            OnScrollEndClicked(this, EventArgs.Empty);
            await _viewModel.PressAsync(button.Key);
        }
    }

    private void OnKeyboardCompleted(object? sender, EventArgs e)
    {
        OnScrollEndClicked(this, EventArgs.Empty);
        _ = _viewModel.PressAsync("enter");
        Reset();
        KeyboardEntry.Focus();
    }

    private void OnControlsClicked(object? sender, EventArgs e) => ControlsPanel.IsVisible = !ControlsPanel.IsVisible;

    private async void OnBackClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");

    private void OnScrollEndClicked(object? sender, EventArgs e)
    {
        _drawable.ScrollOffset = 0;
        ScrollEndButton.IsVisible = false;
        TerminalView.Invalidate();
    }

    private void Reset()
    {
        _resetting = true;
        KeyboardEntry.Text = TerminalKeyboardEdit.Marker;
        KeyboardEntry.CursorPosition = TerminalKeyboardEdit.Marker.Length;
        _resetting = false;
    }
}
