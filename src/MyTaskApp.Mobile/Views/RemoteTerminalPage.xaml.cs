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
    /// <summary>O campo do teclado nunca fica vazio: apagar o marcador é o Backspace.</summary>
    private const string Sentinel = " ";

    private readonly RemoteTerminalViewModel _viewModel;

    private readonly TerminalDrawable _drawable = new();

    private Guid? _sessionId;

    private Guid _deviceId;

    private double _pinchStartFont;

    private double _panStartOffset;

    private bool _resetting;

    public RemoteTerminalPage(RemoteTerminalViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        TerminalView.Drawable = _drawable;
        KeyboardEntry.Text = Sentinel;

        TerminalView.SizeChanged += (_, _) => _ = ResizeAsync();

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (_viewModel.CanType)
            {
                KeyboardEntry.Focus();
            }
        };
        TerminalView.GestureRecognizers.Add(tap);

        var pinch = new PinchGestureRecognizer();
        pinch.PinchUpdated += OnPinch;
        TerminalView.GestureRecognizers.Add(pinch);

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPan;
        TerminalView.GestureRecognizers.Add(pan);
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
        _viewModel.ScreenChanged -= OnScreenChanged;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        base.OnDisappearing();
        await _viewModel.CloseAsync();
    }

    private void OnScreenChanged()
    {
        _drawable.Screen = _viewModel.Screen;
        FitFont();
        TerminalView.Invalidate();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteTerminalViewModel.FontSize))
        {
            FitFont();
            TerminalView.Invalidate();
            _ = ResizeAsync();
        }
        else if (e.PropertyName == nameof(RemoteTerminalViewModel.CanType))
        {
            _drawable.ShowCursor = _viewModel.CanType;
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
                _panStartOffset = _drawable.ScrollOffset;
                break;
            case GestureStatus.Running:
                var lines = (int)(e.TotalY / Math.Max(1, _drawable.CellHeight));
                var maximum = _drawable.Screen?.ScrollbackCount ?? 0;
                _drawable.ScrollOffset = Math.Clamp((int)_panStartOffset + lines, 0, maximum);
                TerminalView.Invalidate();
                break;
        }
    }

    /// <summary>O que o teclado do sistema digitou vai na hora; o marcador apagado é o Backspace.</summary>
    private void OnKeyboardTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_resetting)
        {
            return;
        }

        var text = e.NewTextValue ?? string.Empty;

        if (text.Length < Sentinel.Length)
        {
            _ = _viewModel.BackspaceAsync();
        }
        else if (text.StartsWith(Sentinel, StringComparison.Ordinal) && text.Length > Sentinel.Length)
        {
            _ = _viewModel.TypeTextAsync(text[Sentinel.Length..]);
        }
        else if (!text.StartsWith(Sentinel, StringComparison.Ordinal))
        {
            _ = _viewModel.TypeTextAsync(text);
        }

        Reset();
    }

    private void OnKeyboardCompleted(object? sender, EventArgs e)
    {
        _ = _viewModel.PressAsync("enter");
        Reset();
        KeyboardEntry.Focus();
    }

    private void Reset()
    {
        _resetting = true;
        KeyboardEntry.Text = Sentinel;
        KeyboardEntry.CursorPosition = Sentinel.Length;
        _resetting = false;
    }
}
