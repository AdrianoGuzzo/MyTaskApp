using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Theming;
using MyTaskApp.Desktop.Widget;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A moldura do painel: o modo da janela (normal, HUD, HUD recolhido), a
/// densidade do painel normal e as preferências de janela. Fica separado do
/// <see cref="TodayViewModel"/> porque não é sobre tarefas — e porque o
/// DataContext da janela é o quadro de hoje, então a moldura precisava de um
/// lugar próprio para ser observável.
/// </summary>
/// <remarks>
/// Só decisão: nada aqui posiciona, redimensiona ou chama o sistema. A janela
/// observa e executa (ADR-048).
/// </remarks>
public sealed partial class WidgetChromeViewModel : ObservableObject
{
    /// <summary>Na ordem do enum <see cref="HudPosition"/>: o índice da lista é o valor.</summary>
    private static readonly string[] PositionLabels =
    [
        "Superior esquerdo",
        "Superior direito",
        "Inferior esquerdo",
        "Inferior direito",
        "Centro esquerdo",
        "Centro direito",
        "Personalizada (onde você arrastou)",
    ];

    private static readonly string[] SizeLabels = ["Compacto", "Normal", "Expandido"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpanded))]
    [NotifyPropertyChangedFor(nameof(IsCompact))]
    [NotifyPropertyChangedFor(nameof(IsCollapsed))]
    [NotifyPropertyChangedFor(nameof(IsPanelVisible))]
    [NotifyPropertyChangedFor(nameof(AreGripsVisible))]
    [NotifyPropertyChangedFor(nameof(CollapseGlyph))]
    [NotifyPropertyChangedFor(nameof(CollapseTip))]
    private WidgetMode _mode = WidgetMode.Expanded;

    /// <summary>A máquina de estados da janela (ADR-048). Só os comandos abaixo a movem.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNormal))]
    [NotifyPropertyChangedFor(nameof(IsHud))]
    [NotifyPropertyChangedFor(nameof(IsHudExpanded))]
    [NotifyPropertyChangedFor(nameof(IsHudCollapsed))]
    [NotifyPropertyChangedFor(nameof(IsHeaderVisible))]
    [NotifyPropertyChangedFor(nameof(IsPanelVisible))]
    [NotifyPropertyChangedFor(nameof(AreGripsVisible))]
    [NotifyPropertyChangedFor(nameof(IsWindowTopmost))]
    [NotifyPropertyChangedFor(nameof(IsHudCompact))]
    [NotifyPropertyChangedFor(nameof(IsHudFooterVisible))]
    [NotifyPropertyChangedFor(nameof(IsHudIntroVisible))]
    private WindowMode _windowMode = WindowMode.Normal;

    /// <summary>"Sempre no topo" da janela normal. Não vale no HUD, que tem o seu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindowTopmost))]
    private bool _isTopmost;

    /// <summary>
    /// Sessão, não preferência: no HUD a caixa de captura só existe depois que
    /// o usuário pede, e não sobrevive ao próximo início.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureGlyph))]
    [NotifyPropertyChangedFor(nameof(CaptureTip))]
    [NotifyPropertyChangedFor(nameof(IsHudFooterVisible))]
    private bool _isCaptureOpen;

    /// <summary>Se o app deve subir direto para a bandeja na próxima vez.</summary>
    [ObservableProperty]
    private bool _startHidden;

    /// <summary>
    /// O tema escolhido, ou "seguir o Windows" (ADR-041). A moldura só guarda
    /// a escolha; quem pinta é o <see cref="ThemeController"/>, que o App liga
    /// a esta propriedade.
    /// </summary>
    [ObservableProperty]
    private string _themeId = ThemeCatalog.SystemId;

    /// <summary>
    /// Espelho do registro, não preferência da moldura: <b>não</b> entra no
    /// <c>widget.json</c>. A verdade sobre iniciar com o Windows mora na chave
    /// <c>Run</c> (ADR-023), e uma segunda cópia é como uma delas acaba
    /// diferente.
    /// </summary>
    [ObservableProperty]
    private bool _startsWithWindows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseExits))]
    [NotifyPropertyChangedFor(nameof(CloseHides))]
    [NotifyPropertyChangedFor(nameof(CloseEntersHud))]
    private CloseBehavior _closeBehavior = CloseBehavior.Tray;

    [ObservableProperty]
    private bool _startInHud;

    /// <summary>"Manter o HUD sempre visível". Separado do <see cref="IsTopmost"/> de propósito.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindowTopmost))]
    private bool _hudAlwaysOnTop = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HudPositionIndex))]
    private HudPosition _hudPosition = HudPosition.TopLeft;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HudSizeIndex))]
    [NotifyPropertyChangedFor(nameof(IsHudCompact))]
    [NotifyPropertyChangedFor(nameof(IsHudFooterVisible))]
    private HudSize _hudSize = HudSize.Compact;

    /// <summary>Opacidade do fundo do cartão — nunca do texto, e nunca abaixo do piso.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HudOpacityLabel))]
    private double _hudOpacity = HudSettings.DefaultOpacity;

    [ObservableProperty]
    private bool _hudUseCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHudIntroVisible))]
    private bool _hudIntroSeen;

    [ObservableProperty]
    private bool _useGlobalHotkey;

    /// <summary>Por que o atalho não pôde ser ligado, quando não pôde.</summary>
    [ObservableProperty]
    private string? _hotkeyMessage;

    /// <summary>
    /// O atalho global de "Novo post-it" (ADR-054): o id da combinação, ou
    /// <see cref="HotkeyGesture.Off"/>. Quem registra é a janela.
    /// </summary>
    [ObservableProperty]
    private string _noteHotkey = HotkeyGesture.DefaultNewNote;

    /// <summary>Por que o atalho do post-it não pôde ser ligado, quando não pôde.</summary>
    [ObservableProperty]
    private string? _noteHotkeyMessage;

    /// <summary>
    /// Se o ícone da bandeja subiu. Sem ele "ocultar" não tem caminho de
    /// volta, e o X passa a sair (ADR-016).
    /// </summary>
    [ObservableProperty]
    private bool _trayAvailable;

    /// <summary>
    /// Até o composition root ligar o registro de verdade, a moldura funciona
    /// igual — só não sabe iniciar com o Windows. É o que os testes headless e
    /// o designer precisam.
    /// </summary>
    private IStartupRegistration _startup = UnsupportedStartupRegistration.Instance;

    private bool _canUseGlobalHotkey;

    private bool _canUseNoteHotkey;

    private HudSettings _hud = HudSettings.Default;

    public WidgetChromeViewModel()
    {
        Themes =
        [
            ThemeOptionViewModel.ForSystem(UseTheme),
            .. ThemeCatalog.All.Select(theme => ThemeOptionViewModel.For(theme, UseTheme)),
        ];

        MarkSelectedTheme();

        HudSizeOptions = [.. SizeLabels.Select((label, index) => new HudChoiceViewModel(index, label, value => HudSizeIndex = value))];

        // "Personalizada" não entra no menu: ela nasce de arrastar, não de escolher.
        HudPositionOptions =
        [
            .. PositionLabels.Take((int)HudPosition.Custom)
                .Select((label, index) => new HudChoiceViewModel(index, label, value => HudPositionIndex = value)),
        ];

        MarkHudChoices();
    }

    /// <summary>"Automático" primeiro: é o padrão, e o que a maioria quer.</summary>
    public IReadOnlyList<ThemeOptionViewModel> Themes { get; }

    public IReadOnlyList<HudChoiceViewModel> HudSizeOptions { get; }

    public IReadOnlyList<HudChoiceViewModel> HudPositionOptions { get; }

    public IReadOnlyList<string> HudPositionChoices => PositionLabels;

    public IReadOnlyList<string> HudSizeChoices => SizeLabels;

    public bool IsExpanded => Mode == WidgetMode.Expanded;

    public bool IsCompact => Mode == WidgetMode.Compact;

    public bool IsCollapsed => Mode == WidgetMode.Collapsed;

    public bool IsNormal => WindowMode == WindowMode.Normal;

    /// <summary>HUD aberto ou recolhido: tudo o que não é a janela normal.</summary>
    public bool IsHud => WindowMode != WindowMode.Normal;

    public bool IsHudExpanded => WindowMode == WindowMode.Hud;

    public bool IsHudCollapsed => WindowMode == WindowMode.HudCollapsed;

    public bool IsHudCompact => IsHud && HudSize == HudSize.Compact;

    /// <summary>
    /// A lista à mostra. Na janela normal some só na pílula; no HUD, só no
    /// HUD recolhido.
    /// </summary>
    public bool IsPanelVisible => IsNormal ? Mode != WidgetMode.Collapsed : IsHudExpanded;

    /// <summary>O cabeçalho da janela normal. O HUD tem o próprio, menor.</summary>
    public bool IsHeaderVisible => IsNormal;

    /// <summary>O HUD tem tamanho por preset: alça de redimensionar ali seria promessa vazia.</summary>
    public bool AreGripsVisible => IsNormal && Mode != WidgetMode.Collapsed;

    /// <summary>
    /// O que a janela de fato faz com a ordem Z. Duas preferências, uma para
    /// cada modo, e nenhuma é efeito colateral da outra.
    /// </summary>
    public bool IsWindowTopmost => IsNormal ? IsTopmost : HudAlwaysOnTop;

    /// <summary>"+ Nova tarefa" no pé do HUD; no compacto o "+" do cabeçalho basta.</summary>
    public bool IsHudFooterVisible => IsHudExpanded && HudSize != HudSize.Compact && !IsCaptureOpen;

    public bool IsHudIntroVisible => IsHudExpanded && !HudIntroSeen;

    public bool CloseExits
    {
        get => CloseBehavior == CloseBehavior.Exit;
        set
        {
            if (value)
            {
                CloseBehavior = CloseBehavior.Exit;
            }
        }
    }

    public bool CloseHides
    {
        get => CloseBehavior == CloseBehavior.Tray;
        set
        {
            if (value)
            {
                CloseBehavior = CloseBehavior.Tray;
            }
        }
    }

    public bool CloseEntersHud
    {
        get => CloseBehavior == CloseBehavior.Hud;
        set
        {
            if (value)
            {
                CloseBehavior = CloseBehavior.Hud;
            }
        }
    }

    /// <summary>Índice para o ComboBox: a lista de rótulos segue a ordem do enum.</summary>
    public int HudPositionIndex
    {
        get => (int)HudPosition;
        set
        {
            if (Enum.IsDefined((HudPosition)value))
            {
                HudPosition = (HudPosition)value;
            }
        }
    }

    public int HudSizeIndex
    {
        get => (int)HudSize;
        set
        {
            if (Enum.IsDefined((HudSize)value))
            {
                HudSize = (HudSize)value;
            }
        }
    }

    public string HudOpacityLabel =>
        (HudOpacity * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>O ponto do último arrasto do HUD, em pixels físicos.</summary>
    public PixelPointValue? HudCustomPoint =>
        _hud.X is { } x && _hud.Y is { } y ? new PixelPointValue(x, y) : null;

    /// <summary>Menos para recolher, mais para voltar — o mesmo botão.</summary>
    public string CollapseGlyph => IsCollapsed ? "" : "";

    public string CollapseTip => IsCollapsed ? "Expandir o painel" : "Recolher o painel";

    /// <summary>Mais para abrir a captura, menos para fechá-la — o mesmo botão.</summary>
    public string CaptureGlyph => IsCaptureOpen ? "" : "";

    public string CaptureTip => IsCaptureOpen ? "Fechar a captura" : "Nova tarefa";

    /// <summary>
    /// Fora do Windows o item some do menu, em vez de ficar lá como uma caixa
    /// que nunca marca.
    /// </summary>
    public bool CanStartWithWindows => _startup.IsSupported;

    public bool CanUseGlobalHotkey => _canUseGlobalHotkey;

    public bool CanUseNoteHotkey => _canUseNoteHotkey;

    /// <summary>As escolhas do atalho do post-it, com "Desligado" no fim.</summary>
    public IReadOnlyList<HotkeyChoice> NoteHotkeyChoices { get; } =
    [
        .. HotkeyGesture.NewNoteChoices.Select(gesture => new HotkeyChoice(gesture.Id, gesture.Label)),
        new HotkeyChoice(HotkeyGesture.Off, "Desligado"),
    ];

    /// <summary>
    /// Esconder é da janela, não daqui. O ViewModel só avisa — mesmo desenho do
    /// <c>SettingsRequested</c> do quadro de hoje.
    /// </summary>
    public event Action? HideRequested;

    /// <summary>O X da moldura. A janela fecha, e o <c>Closing</c> decide pelo <see cref="DecideClose"/>.</summary>
    public event Action? CloseRequested;

    /// <summary>Encerrar de verdade, pedido de dentro da janela (o "Sair do MyTaskApp" do HUD).</summary>
    public event Action? ExitRequested;

    public event Action? WindowSettingsRequested;

    /// <summary>
    /// Entrar no HUD: "manter visível sobre as outras janelas", não "ficar
    /// transparente" (ADR-048). Com o HUD recolhido ligado, entra como pílula.
    /// </summary>
    [RelayCommand]
    public void EnterHud()
    {
        if (IsNormal)
        {
            WindowMode = HudUseCollapsed ? WindowMode.HudCollapsed : WindowMode.Hud;
        }
    }

    /// <summary>A porta de volta. Vale do HUD aberto e do recolhido.</summary>
    [RelayCommand]
    public void ExitHud() => WindowMode = WindowMode.Normal;

    [RelayCommand]
    public void ToggleHud()
    {
        if (IsNormal)
        {
            EnterHud();
        }
        else
        {
            ExitHud();
        }
    }

    [RelayCommand]
    public void CollapseHud()
    {
        if (WindowMode == WindowMode.Hud)
        {
            WindowMode = WindowMode.HudCollapsed;
        }
    }

    [RelayCommand]
    public void ExpandHud()
    {
        if (WindowMode == WindowMode.HudCollapsed)
        {
            WindowMode = WindowMode.Hud;
        }
    }

    [RelayCommand]
    public void DismissHudIntro() => HudIntroSeen = true;

    /// <summary>Muda só o "sempre no topo" da janela normal — nada de geometria (ADR-017).</summary>
    [RelayCommand]
    public void ToggleTopmost() => IsTopmost = !IsTopmost;

    /// <summary>O "+" do HUD: a caixa só ocupa espaço depois de pedida.</summary>
    [RelayCommand]
    public void ToggleCapture() => IsCaptureOpen = !IsCaptureOpen;

    /// <summary>
    /// Sair do HUD com a caixa aberta deixaria a captura do HUD por cima da
    /// da janela normal — e recolher com ela aberta esconderia o que se digitava.
    /// </summary>
    partial void OnWindowModeChanged(WindowMode value)
    {
        if (value != WindowMode.Hud)
        {
            IsCaptureOpen = false;
        }
    }

    [RelayCommand]
    public void ToggleCollapsed() =>
        Mode = IsCollapsed ? WidgetMode.Expanded : WidgetMode.Collapsed;

    /// <summary>
    /// Parâmetro em texto para o menu e a bandeja chamarem o mesmo comando, no
    /// mesmo formato que o editor de lembretes já usa para os presets.
    /// </summary>
    [RelayCommand]
    public void UseMode(string? mode) => Mode = mode switch
    {
        "compact" => WidgetMode.Compact,
        "collapsed" => WidgetMode.Collapsed,
        _ => WidgetMode.Expanded,
    };

    [RelayCommand]
    public void UseHudSize(string? size) => HudSize = size switch
    {
        "normal" => HudSize.Normal,
        "expanded" => HudSize.Expanded,
        _ => HudSize.Compact,
    };

    [RelayCommand]
    public void UseHudPosition(string? position) =>
        HudPosition = Enum.TryParse<HudPosition>(position, ignoreCase: true, out var parsed)
            && parsed != HudPosition.Custom
                ? parsed
                : HudPosition.TopLeft;

    /// <summary>
    /// O usuário arrastou o HUD: daqui em diante ele mora onde foi deixado, até
    /// alguém escolher um canto de novo.
    /// </summary>
    public void RememberHudPoint(int x, int y)
    {
        _hud = _hud with { X = x, Y = y };
        HudPosition = HudPosition.Custom;
        OnPropertyChanged(nameof(HudCustomPoint));
    }

    [RelayCommand]
    public void Hide() => HideRequested?.Invoke();

    [RelayCommand]
    public void RequestClose() => CloseRequested?.Invoke();

    [RelayCommand]
    public void RequestExit() => ExitRequested?.Invoke();

    [RelayCommand]
    public void OpenWindowSettings() => WindowSettingsRequested?.Invoke();

    /// <summary>O que um pedido de fechar vira agora.</summary>
    public CloseAction DecideClose() => CloseRouting.Decide(CloseBehavior, WindowMode, TrayAvailable);

    /// <summary>Um id desconhecido vira "seguir o Windows", nunca um tema qualquer.</summary>
    public void UseTheme(string? id) => ThemeId = ThemeCatalog.Normalize(id);

    partial void OnThemeIdChanged(string value) => MarkSelectedTheme();

    partial void OnHudOpacityChanged(double value)
    {
        var clamped = HudSettings.ClampOpacity(value);

        if (clamped != value)
        {
            HudOpacity = clamped;
        }
    }

    partial void OnHudSizeChanged(HudSize value) => MarkHudChoices();

    partial void OnHudPositionChanged(HudPosition value) => MarkHudChoices();

    private void MarkSelectedTheme()
    {
        foreach (var option in Themes)
        {
            option.IsSelected = option.Id == ThemeId;
        }
    }

    private void MarkHudChoices()
    {
        foreach (var option in HudSizeOptions)
        {
            option.IsSelected = option.Value == (int)HudSize;
        }

        foreach (var option in HudPositionOptions)
        {
            option.IsSelected = option.Value == (int)HudPosition;
        }
    }

    /// <summary>
    /// Liga a moldura ao registro do Windows. Só o composition root chama —
    /// mesmo desenho do <c>Attach</c> da janela: sem isto tudo funciona igual,
    /// só não há início automático (ADR-023).
    /// </summary>
    public void UseStartup(IStartupRegistration startup)
    {
        _startup = startup;

        OnPropertyChanged(nameof(CanStartWithWindows));
        StartsWithWindows = startup.IsEnabled;
    }

    /// <summary>Fora do Windows a opção do atalho some, como a de iniciar com ele.</summary>
    public void UseHotkeySupport(bool supported)
    {
        _canUseGlobalHotkey = supported;
        OnPropertyChanged(nameof(CanUseGlobalHotkey));
    }

    /// <summary>O mesmo para o atalho do post-it.</summary>
    public void UseNoteHotkeySupport(bool supported)
    {
        _canUseNoteHotkey = supported;
        OnPropertyChanged(nameof(CanUseNoteHotkey));
    }

    /// <summary>
    /// Comando, e não <c>Mode=TwoWay</c> como o "abrir recolhido": a escrita no
    /// registro pode falhar, e aí o visto precisa contar o que aconteceu de
    /// verdade — não o que foi clicado.
    /// </summary>
    [RelayCommand]
    public void ToggleStartWithWindows()
    {
        if (StartsWithWindows)
        {
            _startup.Disable();
        }
        else
        {
            _startup.Enable();
        }

        // Relê em vez de assumir. É o que faz uma escrita barrada por política
        // devolver a caixa ao estado anterior, em silêncio.
        StartsWithWindows = _startup.IsEnabled;
    }

    /// <summary>
    /// Aplica o que foi lido do disco, sem tocar em geometria. "Iniciar no
    /// HUD" ganha de como a janela estava ao fechar.
    /// </summary>
    public void Restore(WidgetState state)
    {
        _hud = state.Hud;

        Mode = state.Mode;
        IsTopmost = state.Topmost;
        StartHidden = state.StartHidden;
        CloseBehavior = state.CloseBehavior;
        StartInHud = state.StartInHud;
        UseGlobalHotkey = state.GlobalHotkey;
        NoteHotkey = HotkeyGesture.NormalizeNewNote(state.NoteHotkey);
        HudAlwaysOnTop = state.Hud.AlwaysOnTop;
        HudPosition = state.Hud.Position;
        HudSize = state.Hud.Size;
        HudOpacity = state.Hud.Opacity;
        HudUseCollapsed = state.Hud.UseCollapsed;
        HudIntroSeen = state.Hud.IntroSeen;
        UseTheme(state.Theme);

        WindowMode = state.StartInHud && state.WindowMode == WindowMode.Normal
            ? (HudUseCollapsed ? WindowMode.HudCollapsed : WindowMode.Hud)
            : state.WindowMode;

        OnPropertyChanged(nameof(HudCustomPoint));
    }

    /// <summary>Carimba as preferências atuais no estado que vai para o disco.</summary>
    public WidgetState CaptureInto(WidgetState state) => state with
    {
        Mode = Mode,
        Topmost = IsTopmost,
        StartHidden = StartHidden,
        WindowMode = WindowMode,
        CloseBehavior = CloseBehavior,
        StartInHud = StartInHud,
        GlobalHotkey = UseGlobalHotkey,
        NoteHotkey = NoteHotkey,
        Hud = _hud with
        {
            AlwaysOnTop = HudAlwaysOnTop,
            Position = HudPosition,
            Size = HudSize,
            Opacity = HudOpacity,
            UseCollapsed = HudUseCollapsed,
            IntroSeen = HudIntroSeen,
        },
        Theme = ThemeId,
    };
}

/// <summary>Uma combinação de atalho na lista de "Janela e comportamento…".</summary>
public sealed record HotkeyChoice(string Id, string Label);

/// <summary>Um ponto em pixels físicos, sem trazer o Avalonia para quem só quer ler o número.</summary>
public readonly record struct PixelPointValue(int X, int Y);
