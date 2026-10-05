using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Widget;

namespace MyTaskApp.Desktop.Views;

/// <summary>
/// A moldura do widget. O conteúdo é do <see cref="TodayView"/>; aqui mora o
/// que só a janela pode fazer: arrastar, redimensionar, virar HUD e voltar,
/// lembrar onde estava e decidir o que o X significa.
/// </summary>
/// <remarks>
/// A moldura (<see cref="Chrome"/>) decide; a janela executa. Chamada nativa
/// nenhuma mora aqui — passa pelas portas <see cref="IWindowBehaviorService"/>
/// e <see cref="IGlobalHotkeyService"/> (ADR-048).
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>
    /// Arrastar dispara dezenas de <c>PositionChanged</c> por segundo. Gravar
    /// em cada um transformaria mover o painel em centenas de escritas.
    /// </summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>Atravessar a pílula com o mouse a caminho de outra coisa não pode abrir o HUD.</summary>
    private static readonly TimeSpan HoverExpandDelay = TimeSpan.FromMilliseconds(350);

    /// <summary>Sair do HUD por um instante não pode recolhê-lo na cara do usuário.</summary>
    private static readonly TimeSpan IdleCollapseDelay = TimeSpan.FromMilliseconds(1200);

    /// <summary>Quanto o HUD precisa ficar parado para o arrasto ser dado por terminado.</summary>
    private static readonly TimeSpan DragSettleDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Menus e flyouts abertos em qualquer janela. O HUD recolhido não pode se
    /// fechar enquanto o mouse está num menu dele — e o menu é outra janela,
    /// então o ponteiro "sai" do HUD ao entrar nele.
    /// </summary>
    private static readonly HashSet<Popup> OpenPopups = [];

    private readonly DispatcherTimer _save;
    private readonly DispatcherTimer _hoverExpand;
    private readonly DispatcherTimer _idleCollapse;
    private readonly DispatcherTimer _dragSettle;

    private IWidgetStateStore? _store;
    private IWindowBehaviorService _behavior = PortableWindowBehavior.Instance;
    private IGlobalHotkeyService _hotkeys = UnsupportedGlobalHotkey.Instance;
    private bool _hotkeyRegistered;

    /// <summary>
    /// O que vai para o disco. No HUD, a geometria aqui é a da janela normal,
    /// carimbada ao entrar — é ela que volta ao sair.
    /// </summary>
    private WidgetState _state = WidgetState.Default;
    private bool _placed;

    /// <summary>Enquanto a própria janela se ajusta, ninguém grava nada.</summary>
    private bool _adjusting;

    /// <summary>Se a geometria aplicada agora é a do HUD (e não só se a moldura pediu).</summary>
    private bool _hudLayout;

    /// <summary>Onde o HUD foi posto por último; diferente disto depois de um arrasto, o usuário o moveu.</summary>
    private PixelPoint? _hudPlacedAt;

    /// <summary>Um ponto na tela onde o HUD deve morar: a do centro da janela normal, ou a do último arrasto.</summary>
    private PixelPoint? _hudScreenHint;

    /// <summary>
    /// O arrasto do HUD foi iniciado pelo usuário. Só então um
    /// <c>PositionChanged</c> vira "posição personalizada" — uma troca de DPI
    /// também move a janela, e não é escolha de ninguém.
    /// </summary>
    private bool _hudMovedByUser;

    /// <summary>O ponto do arrasto chegando à moldura não deve reposicionar o HUD de volta.</summary>
    private bool _rememberingDrag;

    /// <summary>Saída combinada com o App: o <c>Closing</c> deixa passar.</summary>
    private bool _closingForReal;

    static MainWindow()
    {
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, args) =>
        {
            if (args.GetNewValue<bool>())
            {
                OpenPopups.Add(popup);
            }
            else
            {
                OpenPopups.Remove(popup);
            }
        });
    }

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

        _hoverExpand = new DispatcherTimer { Interval = HoverExpandDelay };
        _hoverExpand.Tick += (_, _) =>
        {
            _hoverExpand.Stop();
            Chrome.ExpandHud();
        };

        _idleCollapse = new DispatcherTimer { Interval = IdleCollapseDelay };
        _idleCollapse.Tick += (_, _) =>
        {
            _idleCollapse.Stop();
            CollapseIfIdle();
        };

        _dragSettle = new DispatcherTimer { Interval = DragSettleDelay };
        _dragSettle.Tick += (_, _) =>
        {
            _dragSettle.Stop();
            SettleHudDrag();
        };

        Chrome.PropertyChanged += OnChromePropertyChanged;
        Chrome.HideRequested += HideAndRemember;
        Chrome.CloseRequested += Close;
        Chrome.ExitRequested += ExitNow;

        // O quadro chega depois do construtor — o composition root e os testes
        // atribuem o DataContext no inicializador —, então o estado do HUD
        // viaja de novo quando ele chega.
        DataContextChanged += (_, _) => ShowPendingOnly();

        PositionChanged += (_, _) => OnPositionChanged();
        SizeChanged += (_, _) => OnGeometryChanged();
        ScalingChanged += (_, _) => OnGeometryChanged();

        PointerEntered += (_, _) => _idleCollapse.Stop();
        PointerExited += (_, _) => ScheduleIdleCollapse();
    }

    /// <summary>
    /// O estado da moldura. Propriedade da janela, e não do
    /// <c>DataContext</c>, porque o DataContext é o quadro de hoje — trocá-lo
    /// quebraria a tela e os testes que a sobem.
    /// </summary>
    public WidgetChromeViewModel Chrome { get; }

    /// <summary>
    /// Como encerrar o app de verdade — parar agendadores, soltar a bandeja.
    /// O composition root preenche; sem ele (testes, designer) "sair" é só
    /// fechar a janela.
    /// </summary>
    public Action? ExitHandler { get; set; }

    /// <summary>As opções do X do HUD estão à vista. Para os testes.</summary>
    internal bool AreCloseChoicesOpen => FlyoutBase.GetAttachedFlyout(HudCloseButton)?.IsOpen == true;

    /// <summary>
    /// Liga a janela ao disco e ao sistema. Só o composition root chama com
    /// tudo: sem isto a janela funciona igual, apenas não lembra de nada e não
    /// fala com o sistema — que é exatamente o que os testes headless precisam.
    /// </summary>
    public void Attach(
        IWidgetStateStore store,
        IWindowBehaviorService? behavior = null,
        IGlobalHotkeyService? hotkeys = null)
    {
        _store = store;
        _behavior = behavior ?? PortableWindowBehavior.Instance;
        _hotkeys = hotkeys ?? UnsupportedGlobalHotkey.Instance;

        Chrome.UseHotkeySupport(_hotkeys.IsSupported);

        _state = store.Load();

        if (_state.X is { } x && _state.Y is { } y)
        {
            _hudScreenHint = new PixelPoint(x, y);
        }

        Chrome.Restore(_state);

        // Só o tamanho agora: posicionar antes de a janela ter handle não é
        // honrado no Windows, e o painel abria no meio da tela.
        ApplyWindowMode(animate: false);
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

        WindowStartupLocation = WindowStartupLocation.Manual;

        if (_hudLayout)
        {
            PlaceHud();
        }
        else
        {
            RestoreGeometry();
        }

        // Só depois de posicionar: daqui para frente o que a janela reporta é
        // escolha do usuário, e vale a pena gravar.
        _placed = true;

        // Grava a escolha do primeiro uso. Sem isto o arquivo só nasceria
        // quando o usuário arrastasse o painel pela primeira vez.
        PersistNow();

        SyncHotkey();
        UpdateInteractiveRegion();

        if (_hudLayout)
        {
            // A altura do HUD acompanha o conteúdo e só existe depois do layout:
            // um HUD ancorado embaixo precisa ser reposto quando ela chegar.
            Dispatcher.UIThread.Post(PlaceHud, DispatcherPriority.Loaded);
        }
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

    /// <summary>
    /// "Sair do MyTaskApp": pelo caminho do App quando ele existe, senão fechando
    /// a janela sem passar pela escolha do X.
    /// </summary>
    public void ExitNow()
    {
        if (ExitHandler is { } exit)
        {
            exit();
            return;
        }

        _closingForReal = true;
        Close();
    }

    /// <summary>
    /// Todo pedido de fechar passa por aqui — o X da moldura, o Alt+F4 e o
    /// "fechar janela" da barra de tarefas — e vira o que "Ao fechar a janela"
    /// mandar (ADR-048). Desligar o Windows e encerrar o app não perguntam nada.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel)
        {
            return;
        }

        if (_closingForReal
            || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            PersistNow();
            return;
        }

        switch (Chrome.DecideClose())
        {
            case CloseAction.Exit when ExitHandler is null:
                PersistNow();
                return;

            case CloseAction.Exit:
                e.Cancel = true;

                // Fora do Closing: o App encerra o lifetime, que fecha esta
                // janela de novo — agora como ApplicationShutdown.
                Dispatcher.UIThread.Post(ExitNow);
                return;

            case CloseAction.HideToTray:
                e.Cancel = true;
                HideAndRemember();
                return;

            case CloseAction.EnterHud:
                e.Cancel = true;
                Chrome.EnterHud();
                return;

            case CloseAction.AskInHud:
                e.Cancel = true;
                OfferCloseChoices();
                return;
        }
    }

    /// <summary>
    /// Já no HUD, com "fechar vira HUD": em vez de ignorar o clique, as três
    /// saídas possíveis, ancoradas no próprio X.
    /// </summary>
    private void OfferCloseChoices()
    {
        Chrome.ExpandHud();

        // No turno seguinte: vindo da pílula, o X ainda não tem layout.
        Dispatcher.UIThread.Post(
            () => FlyoutBase.ShowAttachedFlyout(HudCloseButton),
            DispatcherPriority.Loaded);
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
    /// Só o modo da janela e a densidade mexem em geometria. "Sempre no topo",
    /// opacidade e tema chegam aqui como qualquer outra preferência e não
    /// passam por <see cref="ApplyMode"/> nem pelo HUD (ADR-017, ADR-048).
    /// </summary>
    private void OnChromePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WidgetChromeViewModel.Mode):
                ApplyMode();
                break;

            case nameof(WidgetChromeViewModel.WindowMode):
                ApplyWindowMode(animate: true);
                break;

            case nameof(WidgetChromeViewModel.HudSize) when _hudLayout:
                ShowHudLayout(animate: false);
                break;

            case nameof(WidgetChromeViewModel.HudPosition) when _hudLayout && !_rememberingDrag:
                PlaceHud();
                break;

            case nameof(WidgetChromeViewModel.IsCaptureOpen) when Chrome.IsCaptureOpen:
                // No turno seguinte: a caixa acabou de deixar de ser invisível e
                // ainda não tem layout — Focus() agora não encontraria nada.
                Dispatcher.UIThread.Post(FocusCapture, DispatcherPriority.Loaded);
                break;

            case nameof(WidgetChromeViewModel.UseGlobalHotkey):
                SyncHotkey();
                break;
        }

        ScheduleSave();
    }

    private void FocusCapture() =>
        this.GetVisualDescendants().OfType<TodayView>().FirstOrDefault()?.FocusCapture();

    /// <summary>
    /// No HUD a lista mostra só o que falta: ali altura é o recurso escasso, e
    /// uma tarefa riscada empurra para fora da vista uma que ainda espera.
    /// Quem decide é a moldura, mas quem monta a lista é o quadro de hoje.
    /// Pela mesma razão, o prazo com folga fica só na janela normal (§10).
    /// </summary>
    private void ShowPendingOnly()
    {
        if (DataContext is TodayViewModel board)
        {
            board.HideCompleted = Chrome.IsHud;
            board.PressingDeadlinesOnly = Chrome.IsHud;
        }
    }

    private void ApplyWindowMode(bool animate)
    {
        if (Chrome.IsHud)
        {
            ShowHudLayout(animate);
        }
        else
        {
            ShowNormalLayout(animate);
        }

        ShowPendingOnly();
        UpdateInteractiveRegion();
    }

    /// <summary>
    /// O cartão do HUD: largura do preset, altura do conteúdo até o teto, sem
    /// redimensionar. A janela inteira <b>é</b> o cartão — nada transparente
    /// em volta para roubar clique de quem está atrás.
    /// </summary>
    private void ShowHudLayout(bool animate)
    {
        if (!_hudLayout && _placed)
        {
            // A janela normal volta exatamente para onde estava (ADR-048): a
            // geometria dela é carimbada antes de o cartão tomar o lugar.
            _state = Capture();
            _hudScreenHint = CentreOfWindow();
        }

        _hudLayout = true;
        _hoverExpand.Stop();
        _idleCollapse.Stop();
        _adjusting = true;

        try
        {
            CanResize = false;
            MinWidth = 0;
            MinHeight = 0;

            if (Chrome.IsHudCollapsed)
            {
                SizeToContent = SizeToContent.Manual;
                MaxHeight = double.PositiveInfinity;
                Width = HudMetrics.CollapsedWidth;
                Height = HudMetrics.CollapsedHeight;
            }
            else
            {
                Width = HudMetrics.WidthFor(Chrome.HudSize);
                MaxHeight = HudMetrics.MaxHeightFor(Chrome.HudSize);
                MinHeight = HudMetrics.MinHeight;
                SizeToContent = SizeToContent.Height;
            }
        }
        finally
        {
            _adjusting = false;
        }

        if (!_placed)
        {
            return;
        }

        if (animate)
        {
            FadeIn();
        }

        PlaceHud();

        // De novo depois do layout: só então a altura do conteúdo existe.
        Dispatcher.UIThread.Post(PlaceHud, DispatcherPriority.Loaded);
    }

    /// <summary>A janela de sempre, no tamanho e no lugar em que o usuário a deixou.</summary>
    private void ShowNormalLayout(bool animate)
    {
        var leaving = _hudLayout;

        _hudLayout = false;
        _hoverExpand.Stop();
        _idleCollapse.Stop();
        _dragSettle.Stop();
        _adjusting = true;

        try
        {
            SizeToContent = SizeToContent.Manual;
            MaxHeight = double.PositiveInfinity;
            MinHeight = WidgetMetrics.CollapsedHeight;
        }
        finally
        {
            _adjusting = false;
        }

        ApplyMode();

        if (leaving && _placed)
        {
            if (animate)
            {
                FadeIn();
            }

            RestoreNormalPosition();
        }
    }

    /// <summary>
    /// Entrar e sair do HUD, recolher e abrir: 150ms de opacidade e nada mais.
    /// A queda para zero é instantânea — com a transição ligada ela também
    /// seria animada, e o cartão piscaria em vez de aparecer.
    /// </summary>
    private void FadeIn()
    {
        var transitions = Root.Transitions;

        Root.Transitions = null;
        Root.Opacity = 0;
        Root.Transitions = transitions;

        Dispatcher.UIThread.Post(() => Root.Opacity = 1, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Põe o HUD no canto escolhido da tela onde ele mora, na escala dessa
    /// tela. Chamado de novo a cada mudança de altura: é o que mantém um HUD
    /// ancorado embaixo crescendo para cima.
    /// </summary>
    private void PlaceHud()
    {
        if (!_hudLayout || HudScreen() is not { } screen)
        {
            return;
        }

        var scaling = screen.Scaling;

        var width = Chrome.IsHudCollapsed ? HudMetrics.CollapsedWidth : HudMetrics.WidthFor(Chrome.HudSize);
        var height = Chrome.IsHudCollapsed
            ? HudMetrics.CollapsedHeight
            : ClientSize.Height > 0 ? ClientSize.Height : HudMetrics.MinHeight;

        var custom = Chrome.HudCustomPoint is { } point ? new PixelPoint(point.X, point.Y) : (PixelPoint?)null;

        var rect = HudPlacement.Resolve(
            Chrome.HudPosition,
            screen.WorkingArea,
            ToPixels(width, height, scaling),
            HudPlacement.MarginFor(scaling),
            custom,
            WorkingAreas());

        _adjusting = true;

        try
        {
            Position = rect.TopLeft;
        }
        finally
        {
            _adjusting = false;
        }

        _hudPlacedAt = rect.TopLeft;
        _hudMovedByUser = false;
    }

    /// <summary>
    /// A tela do HUD: a do ponto arrastado, quando ele foi arrastado; senão a
    /// da janela normal no momento em que virou HUD — "o monitor que está
    /// sendo usado", e não sempre o principal.
    /// </summary>
    private Screen? HudScreen()
    {
        if (Chrome.HudPosition == HudPosition.Custom && Chrome.HudCustomPoint is { } point
            && Screens.ScreenFromPoint(new PixelPoint(point.X, point.Y)) is { } dragged)
        {
            return dragged;
        }

        if (_hudScreenHint is { } hint && Screens.ScreenFromPoint(hint) is { } used)
        {
            return used;
        }

        return Screens.Primary ?? Screens.All.FirstOrDefault();
    }

    private PixelPoint CentreOfWindow()
    {
        var size = ToPixels(ClientSize.Width, ClientSize.Height, DesktopScaling);

        return new PixelPoint(Position.X + (size.Width / 2), Position.Y + (size.Height / 2));
    }

    /// <summary>
    /// No HUD o mouse só é da janela dentro do cartão. Com a janela do tamanho
    /// do cartão isso já vale quase inteiro; a região fecha os cantos
    /// arredondados. Na janela normal, a janela inteira volta a ser dela.
    /// </summary>
    private void UpdateInteractiveRegion()
    {
        if (_hudLayout && _placed)
        {
            var radius = Chrome.IsHudCollapsed ? HudMetrics.CollapsedHeight / 2 : HudMetrics.CornerRadius;

            _behavior.SetInteractiveRegion(this, new Rect(ClientSize), radius);
        }
        else
        {
            _behavior.SetInteractiveRegion(this, null, 0);
        }
    }

    private void OnGeometryChanged()
    {
        // Com um arrasto em andamento, reposicionar puxaria o HUD da mão do usuário.
        if (_hudLayout && _placed && !_dragSettle.IsEnabled)
        {
            PlaceHud();
        }

        UpdateInteractiveRegion();
        ScheduleSave();
    }

    private void OnPositionChanged()
    {
        if (!_hudLayout)
        {
            ScheduleSave();
            return;
        }

        if (_hudMovedByUser && !_adjusting)
        {
            _dragSettle.Stop();
            _dragSettle.Start();
        }
    }

    /// <summary>
    /// O HUD parou onde o usuário o soltou: daqui para frente ele mora ali
    /// ("Posição personalizada"), até alguém escolher um canto de novo.
    /// </summary>
    private void SettleHudDrag()
    {
        if (!_hudLayout || _hudPlacedAt is not { } placed || Position == placed)
        {
            return;
        }

        _rememberingDrag = true;

        try
        {
            Chrome.RememberHudPoint(Position.X, Position.Y);
        }
        finally
        {
            _rememberingDrag = false;
        }

        _hudPlacedAt = Position;
        _hudScreenHint = CentreOfWindow();
        ScheduleSave();
    }

    private void ScheduleIdleCollapse()
    {
        if (Chrome.IsHudExpanded && Chrome.HudUseCollapsed)
        {
            _idleCollapse.Stop();
            _idleCollapse.Start();
        }
    }

    /// <summary>
    /// Com o HUD recolhido ligado, o cartão volta a ser pílula quando o mouse
    /// vai embora — salvo se o usuário estiver no meio de alguma coisa nele.
    /// </summary>
    private void CollapseIfIdle()
    {
        if (!Chrome.IsHudExpanded
            || !Chrome.HudUseCollapsed
            || IsPointerOver
            || Chrome.IsCaptureOpen
            || Chrome.IsHudIntroVisible
            || OpenPopups.Any(popup => popup.IsOpen))
        {
            return;
        }

        Chrome.CollapseHud();
    }

    private void OnPillPointerEntered(object? sender, PointerEventArgs e)
    {
        _hoverExpand.Stop();
        _hoverExpand.Start();
    }

    private void OnPillPointerExited(object? sender, PointerEventArgs e) => _hoverExpand.Stop();

    /// <summary>
    /// Liga ou desliga o atalho global conforme a preferência. Se outro
    /// programa já é dono da combinação, a caixa desmarca e a tela diz por quê.
    /// </summary>
    private void SyncHotkey()
    {
        if (!_placed)
        {
            return;
        }

        if (Chrome.UseGlobalHotkey && !_hotkeyRegistered)
        {
            if (_hotkeys.Register(this, OnHotkeyPressed))
            {
                _hotkeyRegistered = true;
                Chrome.HotkeyMessage = null;
            }
            else if (_hotkeys.IsSupported)
            {
                Chrome.HotkeyMessage = $"{_hotkeys.GestureLabel} já está em uso por outro programa.";
                Chrome.UseGlobalHotkey = false;
            }
        }
        else if (!Chrome.UseGlobalHotkey && _hotkeyRegistered)
        {
            _hotkeys.Unregister();
            _hotkeyRegistered = false;
        }
    }

    /// <summary>Normal vira HUD, HUD vira normal — e um app escondido na bandeja aparece.</summary>
    private void OnHotkeyPressed() => Dispatcher.UIThread.Post(() =>
    {
        if (!IsVisible)
        {
            Show();
            WindowState = WindowState.Normal;
        }

        Chrome.ToggleHud();

        if (Chrome.IsNormal)
        {
            Activate();
        }
    });

    /// <summary>
    /// Cada modo tem seu tamanho. O tamanho do modo cheio é o que o usuário
    /// ajustou; os outros dois são fixos, senão "compacto" viraria só um nome
    /// para a janela que ele já tinha. No HUD a densidade fica guardada para a
    /// volta.
    /// </summary>
    private void ApplyMode()
    {
        if (_hudLayout)
        {
            return;
        }

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

    /// <summary>
    /// Saindo do HUD: o canto superior esquerdo de antes, sem mexer no tamanho
    /// que <see cref="ApplyMode"/> acabou de repor. Nunca usado ainda (abriu
    /// direto no HUD), cai no canto do primeiro uso, na tela do HUD.
    /// </summary>
    private void RestoreNormalPosition()
    {
        var screen = _state.X is { } savedX && _state.Y is { } savedY
            ? Screens.ScreenFromPoint(new PixelPoint(savedX, savedY)) ?? FindScreen()
            : FindScreen();

        var scaling = screen?.Scaling ?? 1d;
        var size = ToPixels(Width, Height, scaling);

        var desired = _state.X is { } x && _state.Y is { } y
            ? new PixelRect(x, y, size.Width, size.Height)
            : screen?.WorkingArea is { } corner
                ? WidgetPlacement.DefaultCorner(corner, size)
                : new PixelRect(Position, size);

        _adjusting = true;

        try
        {
            Position = WidgetPlacement.Clamp(desired, WorkingAreas()).TopLeft;
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

    protected override void OnClosed(EventArgs args)
    {
        _save.Stop();
        _hoverExpand.Stop();
        _idleCollapse.Stop();
        _dragSettle.Stop();

        if (_hotkeyRegistered)
        {
            _hotkeys.Unregister();
            _hotkeyRegistered = false;
        }

        base.OnClosed(args);
    }

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
        var state = Chrome.CaptureInto(_state);

        // No HUD a posição e o tamanho correntes são do cartão. A geometria da
        // janela normal fica a que foi carimbada ao entrar.
        if (_hudLayout)
        {
            return state;
        }

        // Antes de posicionar, a posição corrente é a que o Windows escolheu
        // ao abrir: preserva-se a que veio do disco.
        if (_placed)
        {
            state = state with { X = Position.X, Y = Position.Y };
        }

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

    /// <summary>
    /// O arrasto do HUD é sempre nosso — é o que permite separar "o usuário
    /// moveu" de "o sistema moveu". No Windows o <c>BeginMoveDrag</c> só
    /// volta quando o botão é solto; no X11 volta na hora, e quem fecha o
    /// arrasto é o silêncio do <c>PositionChanged</c>.
    /// </summary>
    private void OnHudBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        _hudMovedByUser = true;
        BeginMoveDrag(e);

        _dragSettle.Stop();
        _dragSettle.Start();
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
