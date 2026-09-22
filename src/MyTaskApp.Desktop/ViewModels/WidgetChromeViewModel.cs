using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Widget;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A moldura do painel: modo de exibição e "sempre no topo". Fica separado do
/// <see cref="TodayViewModel"/> porque não é sobre tarefas — e porque o
/// DataContext da janela é o quadro de hoje, então a moldura precisava de um
/// lugar próprio para ser observável.
/// </summary>
public sealed partial class WidgetChromeViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpanded))]
    [NotifyPropertyChangedFor(nameof(IsCompact))]
    [NotifyPropertyChangedFor(nameof(IsCollapsed))]
    [NotifyPropertyChangedFor(nameof(IsPanelVisible))]
    [NotifyPropertyChangedFor(nameof(CollapseGlyph))]
    [NotifyPropertyChangedFor(nameof(CollapseTip))]
    [NotifyPropertyChangedFor(nameof(IsGhostActive))]
    [NotifyPropertyChangedFor(nameof(IsHeaderVisible))]
    private WidgetMode _mode = WidgetMode.Expanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopmostTip))]
    [NotifyPropertyChangedFor(nameof(TopmostGlyph))]
    [NotifyPropertyChangedFor(nameof(GhostPinTip))]
    private bool _isTopmost;

    /// <summary>
    /// Sem moldura e sem cabeçalho: sobram as tarefas sobre a área de trabalho.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGhostActive))]
    [NotifyPropertyChangedFor(nameof(IsHeaderVisible))]
    [NotifyPropertyChangedFor(nameof(GhostTip))]
    private bool _isGhost;

    /// <summary>
    /// Sessão, não preferência: no modo discreto a caixa de captura só existe
    /// depois que o usuário pede, e não sobrevive ao próximo início.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureGlyph))]
    [NotifyPropertyChangedFor(nameof(CaptureTip))]
    private bool _isCaptureOpen;

    /// <summary>Se o app deve subir direto para a bandeja na próxima vez.</summary>
    [ObservableProperty]
    private bool _startHidden;

    /// <summary>
    /// Espelho do registro, não preferência da moldura: <b>não</b> entra no
    /// <c>widget.json</c>. A verdade sobre iniciar com o Windows mora na chave
    /// <c>Run</c> (ADR-023), e uma segunda cópia é como uma delas acaba
    /// diferente.
    /// </summary>
    [ObservableProperty]
    private bool _startsWithWindows;

    /// <summary>
    /// Até o composition root ligar o registro de verdade, a moldura funciona
    /// igual — só não sabe iniciar com o Windows. É o que os testes headless e
    /// o designer precisam.
    /// </summary>
    private IStartupRegistration _startup = UnsupportedStartupRegistration.Instance;

    public bool IsExpanded => Mode == WidgetMode.Expanded;

    public bool IsCompact => Mode == WidgetMode.Compact;

    public bool IsCollapsed => Mode == WidgetMode.Collapsed;

    /// <summary>Recolhido, sobra a pílula: some cabeçalho, captura e lista.</summary>
    public bool IsPanelVisible => Mode != WidgetMode.Collapsed;

    /// <summary>
    /// Recolhido o cabeçalho é a única coisa que sobra na tela — deixar o modo
    /// discreto escondê-lo apagaria o widget inteiro, sem forma de voltar a não
    /// ser pela bandeja.
    /// </summary>
    public bool IsGhostActive => IsGhost && Mode != WidgetMode.Collapsed;

    /// <summary>
    /// No modo discreto não há barra: o painel inteiro vira área de arrasto e o
    /// "+" é o único controle que sobra.
    /// </summary>
    public bool IsHeaderVisible => !IsGhostActive;

    /// <summary>Menos para recolher, mais para voltar — o mesmo botão.</summary>
    public string CollapseGlyph => IsCollapsed ? "\uE710" : "\uE738";

    /// <summary>Alfinete vazado quando solto, preenchido quando fixado.</summary>
    public string TopmostGlyph => IsTopmost ? "\uE840" : "\uE718";

    public string CollapseTip => IsCollapsed ? "Expandir o painel" : "Recolher o painel";

    public string TopmostTip => IsTopmost
        ? "Sempre no topo: ligado"
        : "Manter o painel sempre no topo";

    public string GhostTip => IsGhost
        ? "Modo discreto: ligado"
        : "Sumir com a moldura e deixar só as tarefas";

    /// <summary>
    /// No modo discreto o alfinete é a porta de volta, e não um indicador: sem
    /// cabeçalho ele é o único caminho na janela para desfazer o que o pino
    /// fez. Por isso o texto diz o que o clique faz, e não em que estado está.
    /// </summary>
    public string GhostPinTip => IsTopmost
        ? "Soltar e voltar ao painel com moldura"
        : "Fixar o painel sempre no topo";

    /// <summary>Mais para abrir a captura, menos para fechá-la — o mesmo botão.</summary>
    public string CaptureGlyph => IsCaptureOpen ? "" : "";

    public string CaptureTip => IsCaptureOpen ? "Fechar a captura" : "Adicionar tarefa";

    /// <summary>
    /// Fora do Windows o item some do menu, em vez de ficar lá como uma caixa
    /// que nunca marca.
    /// </summary>
    public bool CanStartWithWindows => _startup.IsSupported;

    /// <summary>
    /// Esconder é da janela, não daqui. O ViewModel só avisa — mesmo desenho do
    /// <c>SettingsRequested</c> do quadro de hoje.
    /// </summary>
    public event Action? HideRequested;

    /// <summary>
    /// Fixar é, na prática, "deixa isso aí sem me atrapalhar": o modo discreto
    /// vem junto. Os dois continuam separáveis — <see cref="ToggleGhost"/>
    /// devolve a moldura sem soltar o pino.
    /// </summary>
    [RelayCommand]
    public void ToggleTopmost()
    {
        IsTopmost = !IsTopmost;
        IsGhost = IsTopmost;
    }

    [RelayCommand]
    public void ToggleGhost() => IsGhost = !IsGhost;

    /// <summary>O "+" do modo discreto: a caixa só ocupa espaço depois de pedida.</summary>
    [RelayCommand]
    public void ToggleCapture() => IsCaptureOpen = !IsCaptureOpen;

    /// <summary>
    /// Voltar para a moldura com a caixa aberta deixaria duas capturas na tela:
    /// a do cabeçalho e a do modo discreto.
    /// </summary>
    partial void OnIsGhostChanged(bool value)
    {
        if (!value)
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
    public void Hide() => HideRequested?.Invoke();

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

    /// <summary>Aplica o que foi lido do disco, sem tocar em geometria.</summary>
    public void Restore(WidgetState state)
    {
        Mode = state.Mode;
        IsTopmost = state.Topmost;
        IsGhost = state.Ghost;
        StartHidden = state.StartHidden;
    }

    /// <summary>Carimba as preferências atuais no estado que vai para o disco.</summary>
    public WidgetState CaptureInto(WidgetState state) => state with
    {
        Mode = Mode,
        Topmost = IsTopmost,
        Ghost = IsGhost,
        StartHidden = StartHidden,
    };
}
