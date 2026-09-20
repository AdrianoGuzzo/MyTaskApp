using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Desktop.Widget;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O pino fixa o painel sobre as outras janelas e liga o modo discreto — e
/// nada além disso. O que estes testes guardam é o "nada além disso": é fácil
/// alguém ler "fixar" como "prender" e transformá-lo numa trava de posição, e
/// um widget que o usuário não consegue tirar da frente é pior do que um que
/// não fica no topo.
/// </summary>
public class WidgetPinTests
{
    private static readonly DateOnly Date = new(2026, 9, 17);

    /// <summary>
    /// A tela headless é 1920x1280 com escala 1: um painel deste tamanho cabe
    /// inteiro, então o clamp é identidade e não polui as asserções de posição.
    /// </summary>
    private static readonly PixelPoint Somewhere = new(500, 300);

    private static TodayTask Row(string title) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, null, false);

    private static async Task<(MainWindow Window, FakeUseCaseRunner Runner)> ShowAsync(
        params TodayTask[] pending)
    {
        var runner = new FakeUseCaseRunner
        {
            Result = new TodayBoard(Date, [], [], [.. pending], [], []),
        };

        var viewModel = new TodayViewModel(
            runner, TimeProvider.System, NullLogger<TodayViewModel>.Instance);

        await viewModel.LoadAsync(CancellationToken.None);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Settle(window);

        // Sem store a janela não se posiciona sozinha (PlaceOnce sai cedo): daqui
        // para frente a posição é só o que o teste escrever.
        window.Position = Somewhere;

        return (window, runner);
    }

    /// <summary>
    /// Os três passos são necessários. Redimensionar no headless é postado no
    /// dispatcher; rodar a fila invalida o layout que acabou de ser calculado; e
    /// o <c>InputHitTest</c> responde pela árvore de composição, que só alcança
    /// o layout num render. Sem o quadro forçado, trocar de modo deixa o
    /// hit-test apontando para onde os elementos estavam antes — e um teste de
    /// clique falha sem que haja nada errado no app.
    /// </summary>
    private static void Settle(MainWindow window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static Border TitleBar(Visual window) =>
        window.GetVisualDescendants()
            .OfType<Border>()
            .Single(border => border.Classes.Contains("titleBar"));

    private static Border Shell(Visual window) =>
        window.GetVisualDescendants()
            .OfType<Border>()
            .Single(border => border.Classes.Contains("widgetShell"));

    private static IReadOnlyList<Border> Grips(Visual window) =>
        window.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => WindowDecorationProperties.GetElementRole(border)
                is WindowDecorationsElementRole.ResizeN
                or WindowDecorationsElementRole.ResizeS
                or WindowDecorationsElementRole.ResizeE
                or WindowDecorationsElementRole.ResizeW
                or WindowDecorationsElementRole.ResizeNE
                or WindowDecorationsElementRole.ResizeNW
                or WindowDecorationsElementRole.ResizeSE
                or WindowDecorationsElementRole.ResizeSW)
            .ToList();

    /// <summary>
    /// Como a plataforma resolve um clique: acha o elemento sob o ponteiro e
    /// sobe até o primeiro papel declarado. É isso que decide entre "arrastar a
    /// janela" e "entregar o clique ao controle" — e não o elemento em si, que
    /// pode muito bem ser um ScrollViewer sem papel nenhum.
    /// </summary>
    private static WindowDecorationsElementRole RoleAt(MainWindow window, Point point) =>
        (window.InputHitTest(point) as Visual)?
            .GetSelfAndVisualAncestors()
            .Select(WindowDecorationProperties.GetElementRole)
            .FirstOrDefault(role => role != WindowDecorationsElementRole.None)
        ?? WindowDecorationsElementRole.None;

    private static Point Centre(Visual target, Visual window) =>
        target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
        ?? throw new InvalidOperationException("Elemento fora da árvore visual.");

    private static void Click(MainWindow window, Point point)
    {
        window.MouseMove(point, RawInputModifiers.None);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task PinningTheWindow_LeavesTheGeometryExactlyWhereItWas()
    {
        var (window, _) = await ShowAsync(Row("Deploy"));

        // Fora das medidas que ApplyMode() reporia (360x560): é isto que torna o
        // teste capaz de falhar se o pino passar a reaplicar o modo.
        window.Width = 500;
        window.Height = 500;
        Settle(window);

        var position = window.Position;
        var width = window.Width;
        var height = window.Height;
        var canResize = window.CanResize;
        var state = window.WindowState;

        window.Chrome.ToggleTopmost();
        Settle(window);

        window.Topmost.Should().BeTrue();
        window.Position.Should().Be(position);
        window.Width.Should().Be(width);
        window.Height.Should().Be(height);
        window.CanResize.Should().Be(canResize);
        window.WindowState.Should().Be(state);
    }

    [AvaloniaFact]
    public async Task PinningInCompact_DoesNotReapplyTheMode()
    {
        // A trava estrutural. ApplyMode() no compacto recalcula a altura a partir
        // do estado (teto de 420): se o pino chegasse lá, 300 viraria 420.
        var (window, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.UseMode("compact");
        Settle(window);

        window.Height = 300;
        Settle(window);

        window.Chrome.ToggleTopmost();
        Settle(window);

        window.Height.Should().Be(300);
        window.Topmost.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Pinned_TheWindowStillMoves()
    {
        // O pouso de um arrasto. Se o pino fosse trava de posição, alguma coisa
        // traria a janela de volta — e o usuário não a tiraria mais da frente.
        var (window, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleTopmost();
        Settle(window);

        var landing = new PixelPoint(900, 480);
        window.Position = landing;

        window.Position.Should().Be(landing);

        // E mover não solta o pino.
        window.Topmost.Should().BeTrue();

        // Nem soltar o pino é ponto de restauração: a janela fica onde está.
        window.Chrome.ToggleTopmost();
        Settle(window);

        window.Position.Should().Be(landing);
    }

    [AvaloniaFact]
    public async Task Pinned_TheDragAreaMovesToThePanelItself()
    {
        // Fixar liga o modo discreto, e o modo discreto tira o cabeçalho. A
        // janela não pode ficar sem forma de ser arrastada por causa disso: o
        // painel inteiro assume o papel que era da barra.
        var (window, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleTopmost();
        Settle(window);

        TitleBar(window).IsEffectivelyVisible.Should().BeFalse();

        var shell = Shell(window);

        WindowDecorationProperties.GetElementRole(shell)
            .Should().Be(WindowDecorationsElementRole.TitleBar);

        // E a área vazia arrasta de verdade: o clique pode cair num
        // ScrollViewer sem papel, mas a subida resolve em TitleBar. Com fundo
        // nulo no painel ela não receberia ponteiro nenhum e o arrasto por
        // espaço vazio simplesmente não existiria.
        RoleAt(window, new Point(window.Width / 2, window.Height - 90))
            .Should().Be(WindowDecorationsElementRole.TitleBar);
    }

    [AvaloniaFact]
    public async Task PinnedWithoutGhost_TheTitleBarIsStillTheDragHandle()
    {
        // Os dois são separáveis: devolver a moldura não pode soltar o pino.
        var (window, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleTopmost();
        window.Chrome.ToggleGhost();
        Settle(window);

        window.Topmost.Should().BeTrue();
        window.Chrome.IsGhost.Should().BeFalse();

        var titleBar = TitleBar(window);

        titleBar.IsEffectivelyVisible.Should().BeTrue();
        WindowDecorationProperties.GetElementRole(titleBar)
            .Should().Be(WindowDecorationsElementRole.TitleBar);

        // A rede de segurança continua alcançável: o ponteiro chega à barra, que
        // é quem chama BeginMoveDrag. O arrasto em si não é afirmado — no
        // headless BeginMoveDrag é inerte, e "a janela se moveu" passaria com ou
        // sem manipulador.
        var pressed = 0;
        titleBar.PointerPressed += (_, _) => pressed++;

        var grab = titleBar.TranslatePoint(new Point(4, 4), window)!.Value;
        var position = window.Position;

        Click(window, grab);

        pressed.Should().Be(1);
        window.Topmost.Should().BeTrue();
        window.Position.Should().Be(position);
    }

    [AvaloniaTheory]
    [InlineData("expanded")]
    [InlineData("compact")]
    public async Task Pinned_TheResizeGripsStayLive(string mode)
    {
        var (window, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleTopmost();
        window.Chrome.UseMode(mode);
        Settle(window);

        window.CanResize.Should().BeTrue();
        Grips(window).Should().HaveCount(8).And.OnlyContain(grip => grip.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Collapsed_HasNoGripsAndNoResize_PinOrNot()
    {
        // O tamanho fixo da pílula é decisão do modo, nunca do pino.
        var (window, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.UseMode("collapsed");
        window.Chrome.ToggleTopmost();
        Settle(window);

        window.CanResize.Should().BeFalse();
        Grips(window).Should().OnlyContain(grip => !grip.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Pinned_TheChecklistStillTakesClicks()
    {
        var (window, runner) = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleTopmost();
        Settle(window);

        var checkbox = window.GetVisualDescendants().OfType<CheckBox>().Single();
        var centre = Centre(checkbox, window);

        // Com o painel inteiro marcado como área de arrasto, é o ElementRole="User"
        // das linhas que mantém o clique chegando ao checklist. Sem ele, marcar
        // uma tarefa viraria um empurrão na janela.
        var hit = window.InputHitTest(centre) as Visual;

        hit.Should().NotBeNull();
        hit!.FindAncestorOfType<CheckBox>(includeSelf: true).Should().BeSameAs(checkbox);
        RoleAt(window, centre).Should().Be(WindowDecorationsElementRole.User);

        Click(window, centre);

        runner.Invoked.Should().Contain(typeof(CompleteOccurrenceHandler));
        window.Topmost.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task FocusingTheWindow_DoesNotDropThePin()
    {
        // Clicar na janela para trazê-la ao foco não pode desligar o pino. O
        // headless nunca levanta Deactivated, então perder o foco fica para a
        // conferência manual; o que dá para guardar aqui é que ninguém
        // acrescentou um manipulador de Activated que mexa no Topmost.
        var (window, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleTopmost();
        Settle(window);

        window.Activate();
        Dispatcher.UIThread.RunJobs();

        Click(window, new Point(window.Width / 2, window.Height - 90));

        window.Topmost.Should().BeTrue();
        window.Chrome.IsTopmost.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task ThePinSurvivesEveryModeChange()
    {
        var (window, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.ToggleTopmost();

        foreach (var mode in new[] { "compact", "collapsed", "expanded" })
        {
            window.Chrome.UseMode(mode);
            Settle(window);

            window.Topmost.Should().BeTrue();
            window.Chrome.IsTopmost.Should().BeTrue();
        }
    }

    [AvaloniaFact]
    public void ThePinChangesNothingOnDiskButItselfAndTheGhost()
    {
        // WidgetState é record, então a igualdade compara campo a campo: se ligar
        // o pino mexesse em X, Y, Width, Height ou Mode, este teste quebraria
        // dizendo exatamente qual campo mudou.
        var store = new RecordingStore(WidgetState.Default with { X = 500, Y = 300 });
        var window = new MainWindow();

        window.Attach(store);
        window.Show();
        Settle(window);

        // Geometria fora dos padrões de propósito: comparar dois registros cheios
        // de valores default esconderia uma troca que zerasse campos.
        window.Width = 500;
        Settle(window);

        window.PersistNow();
        var placed = store.Saved;

        window.Chrome.ToggleTopmost();
        window.PersistNow();

        store.Saved.Should().Be(placed with { Topmost = true, Ghost = true });
    }

    [AvaloniaFact]
    public void Compact_LetsTheUserResize_ButOnlyTheFullPanelDefinesTheSavedSize()
    {
        // Decisão registrada no ADR-017, não esquecimento: o gesto é permitido —
        // é o que o item 8 pede — mas WidgetState tem um par Width/Height só, e
        // gravar a altura do compacto apagaria a do painel inteiro.
        var store = new RecordingStore(
            WidgetState.Default with { X = 500, Y = 300, Width = 380, Height = 600 });

        var window = new MainWindow();

        window.Attach(store);
        window.Show();
        Settle(window);

        window.Chrome.UseMode("compact");
        Settle(window);

        window.CanResize.Should().BeTrue();

        window.Height = 300;
        Settle(window);
        window.PersistNow();

        store.Saved.Width.Should().Be(380);
        store.Saved.Height.Should().Be(600);
    }

    /// <summary>Lembra o que foi gravado, sem tocar no disco.</summary>
    private sealed class RecordingStore : IWidgetStateStore
    {
        private readonly WidgetState _initial;

        public RecordingStore(WidgetState initial)
        {
            _initial = initial;
            Saved = initial;
        }

        public WidgetState Saved { get; private set; }

        public WidgetState Load() => _initial;

        public void Save(WidgetState state) => Saved = state;
    }
}
