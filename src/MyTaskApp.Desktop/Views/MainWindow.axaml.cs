using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Widget;

namespace MyTaskApp.Desktop.Views;

/// <summary>
/// A moldura do widget. O conteúdo é do <see cref="TodayView"/>; aqui mora o
/// que só a janela pode fazer: arrastar, redimensionar, trocar de modo e
/// lembrar onde estava.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Arrastar dispara dezenas de <c>PositionChanged</c> por segundo. Gravar
    /// em cada um transformaria mover o painel em centenas de escritas.
    /// </summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly DispatcherTimer _save;

    private IWidgetStateStore? _store;
    private WidgetState _state = WidgetState.Default;
    private bool _placed;

    /// <summary>Enquanto a própria janela se ajusta, ninguém grava nada.</summary>
    private bool _adjusting;

    public MainWindow()
    {
        // Antes do InitializeComponent: o XAML liga em #Shell.Chrome já na carga.
        Chrome = new WidgetChromeViewModel();

        InitializeComponent();

        _save = new DispatcherTimer { Interval = SaveDelay };
        _save.Tick += (_, _) =>
        {
            _save.Stop();
            PersistNow();
        };

        Chrome.PropertyChanged += OnChromePropertyChanged;
        Chrome.HideRequested += HideAndRemember;

        // O quadro chega depois do construtor — o composition root e os testes
        // atribuem o DataContext no inicializador —, então o estado do pino
        // viaja de novo quando ele chega.
        DataContextChanged += (_, _) => ShowPendingOnly();

        PositionChanged += (_, _) => ScheduleSave();
        SizeChanged += (_, _) => ScheduleSave();
    }

    /// <summary>
    /// O estado da moldura. Propriedade da janela, e não do
    /// <c>DataContext</c>, porque o DataContext é o quadro de hoje — trocá-lo
    /// quebraria a tela e os testes que a sobem.
    /// </summary>
    public WidgetChromeViewModel Chrome { get; }

    /// <summary>
    /// Liga a janela ao disco. Só o composition root chama: sem isto a janela
    /// funciona igual, apenas não lembra de nada — que é exatamente o que os
    /// testes headless precisam.
    /// </summary>
    public void Attach(IWidgetStateStore store)
    {
        _store = store;
        _state = store.Load();

        Chrome.Restore(_state);

        // Só o tamanho agora: posicionar antes de a janela ter handle não é
        // honrado no Windows, e o painel abria no meio da tela.
        ApplyMode();
    }

    protected override void OnOpened(EventArgs args)
    {
        base.OnOpened(args);

        if (_store is null || _placed)
        {
            return;
        }

        // No turno seguinte: o Avalonia ainda aplica o próprio posicionamento
        // de abertura depois deste evento, e sobrescreveria o nosso.
        Dispatcher.UIThread.Post(PlaceOnce, DispatcherPriority.Loaded);
    }

    private void PlaceOnce()
    {
        if (_placed)
        {
            return;
        }

        RestoreGeometry();

        // Só depois de posicionar: daqui para frente o que a janela reporta é
        // escolha do usuário, e vale a pena gravar.
        _placed = true;

        // Grava a escolha do primeiro uso. Sem isto o arquivo só nasceria
        // quando o usuário arrastasse o painel pela primeira vez.
        PersistNow();
    }

    /// <summary>
    /// Sumir é o último gesto antes de o usuário esquecer que o app existe — e
    /// pode ser o último antes de desligar a máquina.
    /// </summary>
    public void HideAndRemember()
    {
        PersistNow();
        Hide();
    }

    /// <summary>Grava agora. Usado ao esconder e ao encerrar.</summary>
    public void PersistNow()
    {
        if (_store is null)
        {
            return;
        }

        _state = Capture();
        _store.Save(_state);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Widget maximizado deixa de ser widget. O gesto existe no sistema
        // (duplo clique na área de arrasto) e aqui ele simplesmente não vale.
        if (change.Property == WindowStateProperty
            && change.GetNewValue<WindowState>() == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }
    }

    /// <summary>
    /// Só o modo mexe em geometria. O pino ("sempre no topo") chega aqui como
    /// qualquer outra preferência e não pode passar por <see cref="ApplyMode"/>:
    /// fixar o painel acima das outras janelas não é travar onde ele está.
    /// </summary>
    private void OnChromePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WidgetChromeViewModel.Mode))
        {
            ApplyMode();
        }

        if (e.PropertyName == nameof(WidgetChromeViewModel.IsTopmost))
        {
            ShowPendingOnly();
        }

        if (e.PropertyName == nameof(WidgetChromeViewModel.IsCaptureOpen)
            && Chrome.IsCaptureOpen)
        {
            // No turno seguinte: a caixa acabou de deixar de ser invisível e
            // ainda não tem layout — Focus() agora não encontraria nada.
            Dispatcher.UIThread.Post(FocusCapture, DispatcherPriority.Loaded);
        }

        ScheduleSave();
    }

    private void FocusCapture() =>
        this.GetVisualDescendants().OfType<TodayView>().FirstOrDefault()?.FocusCapture();

    /// <summary>
    /// Fixado, o painel fica num canto sobre as outras janelas e cada linha
    /// custa altura: as concluídas saem da lista e sobram as pendentes. Quem
    /// decide é a moldura — é o pino que muda —, mas quem monta a lista é o
    /// quadro de hoje, então a decisão viaja para lá.
    /// </summary>
    private void ShowPendingOnly()
    {
        if (DataContext is TodayViewModel board)
        {
            board.HideCompleted = Chrome.IsTopmost;
        }
    }

    /// <summary>
    /// Cada modo tem seu tamanho. O tamanho do modo cheio é o que o usuário
    /// ajustou; os outros dois são fixos, senão "compacto" viraria só um nome
    /// para a janela que ele já tinha.
    /// </summary>
    private void ApplyMode()
    {
        _adjusting = true;

        try
        {
            switch (Chrome.Mode)
            {
                case WidgetMode.Collapsed:
                    CanResize = false;

                    // O mínimo do painel é maior que a pílula: sem baixá-lo, o
                    // recolhido nunca chegaria ao tamanho que ele promete.
                    MinWidth = WidgetMetrics.CollapsedWidth;
                    Width = WidgetMetrics.CollapsedWidth;
                    Height = WidgetMetrics.CollapsedHeight;
                    break;

                case WidgetMode.Compact:
                    CanResize = true;
                    MinWidth = WidgetMetrics.MinWidth;
                    Width = _state.Width;
                    Height = Math.Min(_state.Height, WidgetMetrics.CompactHeight);
                    break;

                default:
                    CanResize = true;
                    MinWidth = WidgetMetrics.MinWidth;
                    Width = _state.Width;
                    Height = _state.Height;
                    break;
            }
        }
        finally
        {
            _adjusting = false;
        }

        // Mudar de tamanho perto da borda pode jogar o painel para fora.
        ClampToScreen();
    }

    private void RestoreGeometry()
    {
        _adjusting = true;

        try
        {
            WindowStartupLocation = WindowStartupLocation.Manual;

            var screen = FindScreen();
            var scaling = screen?.Scaling ?? 1d;
            var area = screen?.WorkingArea;

            var size = ToPixels(_state.Width, _state.Height, scaling);

            // Sem posição salva é o primeiro uso: encosta perto do relógio.
            var desired = _state.X is { } x && _state.Y is { } y
                ? new PixelRect(x, y, size.Width, size.Height)
                : area is { } corner
                    ? WidgetPlacement.DefaultCorner(corner, size)
                    : new PixelRect(PixelPoint.Origin, size);

            Apply(WidgetPlacement.Clamp(desired, WorkingAreas()), scaling);
        }
        finally
        {
            _adjusting = false;
        }
    }

    private void ClampToScreen()
    {
        var screen = FindScreen();
        var scaling = screen?.Scaling ?? 1d;
        var size = ToPixels(Width, Height, scaling);

        _adjusting = true;

        try
        {
            Apply(
                WidgetPlacement.Clamp(
                    new PixelRect(Position.X, Position.Y, size.Width, size.Height),
                    WorkingAreas()),
                scaling);
        }
        finally
        {
            _adjusting = false;
        }
    }

    private void Apply(PixelRect rect, double scaling)
    {
        Position = rect.TopLeft;

        // Recolhido é tamanho fixo: deixar o clamp encolher a pílula a
        // transformaria num risco de 12px em telas pequenas.
        if (Chrome.IsCollapsed)
        {
            return;
        }

        Width = rect.Width / scaling;
        Height = rect.Height / scaling;
    }

    /// <summary>
    /// <c>Position</c> é pixel físico; <c>Width</c>/<c>Height</c> são unidades
    /// independentes de DPI. Misturar os dois é o que faz o painel encolher a
    /// cada reabertura num monitor a 150%.
    /// </summary>
    private static PixelSize ToPixels(double width, double height, double scaling)
    {
        var safeWidth = double.IsFinite(width) ? width : WidgetMetrics.DefaultWidth;
        var safeHeight = double.IsFinite(height) ? height : WidgetMetrics.DefaultHeight;

        return new PixelSize(
            (int)Math.Round(safeWidth * scaling),
            (int)Math.Round(safeHeight * scaling));
    }

    private Screen? FindScreen() =>
        Screens.ScreenFromPoint(Position) ?? Screens.Primary;

    private IReadOnlyList<PixelRect> WorkingAreas() =>
        [.. Screens.All.Select(screen => screen.WorkingArea)];

    private void ScheduleSave()
    {
        // Enquanto a posição não foi restaurada, o que a janela reporta é onde
        // o Windows a jogou ao abrir — gravar isso apagaria o que o usuário
        // escolheu na sessão anterior.
        if (_store is null || _adjusting || !_placed)
        {
            return;
        }

        _save.Stop();
        _save.Start();
    }

    private WidgetState Capture()
    {
        // Antes de posicionar, a posição corrente é a que o Windows escolheu
        // ao abrir: preserva-se a que veio do disco.
        var state = _placed
            ? Chrome.CaptureInto(_state) with { X = Position.X, Y = Position.Y }
            : Chrome.CaptureInto(_state);

        // Só o modo cheio define o tamanho lembrado: gravar a altura do modo
        // compacto apagaria para sempre o tamanho que o usuário escolheu.
        return Chrome.IsExpanded
            ? state with { Width = ClientSize.Width, Height = ClientSize.Height }
            : state;
    }

    /// <summary>
    /// Rede de segurança do arrasto. Se a plataforma honrar o
    /// <c>ElementRole</c>, o sistema move a janela e este manipulador nem
    /// recebe o ponteiro.
    /// </summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Clique em botão é clique em botão, não início de arrasto.
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanResize || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is Control { Tag: string edge } && Enum.TryParse<WindowEdge>(edge, out var side))
        {
            BeginResizeDrag(side, e);
        }
    }
}
