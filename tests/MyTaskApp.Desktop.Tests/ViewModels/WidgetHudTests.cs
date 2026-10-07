using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Desktop.Widget;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O HUD desenhado de verdade (ADR-048). O que se guarda aqui é o que o pino
/// antigo errava: janela que some, área vazia que rouba clique, X que faz
/// coisa inesperada, e a janela normal que não volta para onde estava.
/// </summary>
public class WidgetHudTests
{
    private static readonly DateOnly Date = new(2026, 9, 17);

    private static readonly WidgetState Placed = WidgetState.Default with { X = 500, Y = 300 };

    private static TodayTask Row(string title) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, null, false);

    private static async Task<(MainWindow Window, RecordingStore Store, RecordingBehavior Behavior)> ShowAsync(
        IReadOnlyList<TodayTask> pending,
        IReadOnlyList<TodayTask>? completed = null,
        WidgetState? state = null)
    {
        var viewModel = new TodayViewModel(
            new FakeUseCaseRunner { Result = new TodayBoard(Date, [], [], pending, [], completed ?? []) },
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

        await viewModel.LoadAsync(CancellationToken.None);

        var store = new RecordingStore(state ?? Placed);
        var behavior = new RecordingBehavior();

        // A ordem do composition root: o quadro entra antes de o disco ser lido.
        var window = new MainWindow { DataContext = viewModel };
        window.Attach(store, behavior);
        window.Show();
        Settle(window);

        return (window, store, behavior);
    }

    private static Task<(MainWindow Window, RecordingStore Store, RecordingBehavior Behavior)> ShowAsync(
        params TodayTask[] pending) => ShowAsync(pending, null, null);

    /// <summary>Ver <c>WidgetPinTests.Settle</c>. Duas voltas: o HUD se reposiciona depois do layout.</summary>
    private static void Settle(MainWindow window)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static T Named<T>(Visual window, Func<T, bool> match)
        where T : Visual =>
        window.GetVisualDescendants().OfType<T>().Single(match);

    private static Border WithClass(Visual window, string name) =>
        Named<Border>(window, border => border.Classes.Contains(name));

    private static IReadOnlyList<string> VisibleTexts(Visual window) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(block.Text))
            .Select(block => block.Text!)
            .ToList();

    private static PixelRect WorkingArea(MainWindow window) => window.Screens.Primary!.WorkingArea;

    private static int Margin(MainWindow window) => HudPlacement.MarginFor(window.Screens.Primary!.Scaling);

    private static PixelSize PixelSizeOf(MainWindow window) =>
        PixelSize.FromSize(window.ClientSize, window.Screens.Primary!.Scaling);

    [AvaloniaFact]
    public async Task EnteringTheHud_MakesACompactCardInTheTopLeftCorner_OnTop()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.EnterHud();
        Settle(window);

        window.Width.Should().Be(HudMetrics.WidthFor(HudSize.Compact));
        window.CanResize.Should().BeFalse();
        window.Topmost.Should().BeTrue();

        var area = WorkingArea(window);
        window.Position.Should().Be(new PixelPoint(area.X + Margin(window), area.Y + Margin(window)));

        WithClass(window, "titleBar").IsEffectivelyVisible.Should().BeFalse();
        WithClass(window, "hudBar").IsEffectivelyVisible.Should().BeTrue();
        VisibleTexts(window).Should().Contain("Deploy");
    }

    [AvaloniaFact]
    public async Task TheHud_IsTheCardAndNothingElse_SoNoEmptyAreaStealsClicks()
    {
        // No Windows quem recebe o clique é o retângulo da janela, e não o pixel
        // desenhado. A margem de 10px da sombra, na janela normal, é área
        // transparente que bloqueia o mouse; no HUD ela não pode existir.
        var (window, _, _) = await ShowAsync(Row("Deploy"));

        WithClass(window, "widgetShell").Margin.Should().Be(new Thickness(10));

        window.Chrome.EnterHud();
        Settle(window);

        var shell = WithClass(window, "widgetShell");

        shell.Margin.Should().Be(default(Thickness));
        shell.Bounds.Size.Should().Be(window.ClientSize);
    }

    [AvaloniaFact]
    public async Task TheHud_IsNotATransparentWindow()
    {
        // O antigo modo discreto apagava o fundo. O HUD tem fundo de verdade:
        // o tema, numa camada com a opacidade escolhida — nunca abaixo do piso.
        var (window, _, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.HudOpacity = 0.1;
        window.Chrome.EnterHud();
        Settle(window);

        var backdrop = WithClass(window, "hudBackdrop");

        backdrop.IsEffectivelyVisible.Should().BeTrue();
        backdrop.Opacity.Should().Be(HudSettings.MinOpacity);
        (backdrop.Background as ISolidColorBrush)!.Color.A.Should().Be(255);
    }

    [AvaloniaFact]
    public async Task TheOpacity_ReachesTheBackgroundButNotTheText()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.EnterHud();
        window.Chrome.HudOpacity = 0.8;
        Settle(window);

        WithClass(window, "hudBackdrop").Opacity.Should().Be(0.8);

        // O texto está em outra camada: a opacidade não desce até ele.
        var title = Named<TextBlock>(window, block => block.Text == "Deploy");
        title.GetSelfAndVisualAncestors().OfType<Visual>()
            .Where(visual => visual is not Panel { Name: "Root" })
            .Should().OnlyContain(visual => visual.Opacity == 1);
    }

    [AvaloniaFact]
    public async Task TheHudHeight_FollowsTheContent_UpToThePresetCeiling()
    {
        var (few, _, _) = await ShowAsync(Row("Deploy"));
        few.Chrome.HudIntroSeen = true;
        few.Chrome.EnterHud();
        Settle(few);

        var (many, _, _) = await ShowAsync([.. Enumerable.Range(1, 15).Select(index => Row($"Tarefa {index}"))]);
        many.Chrome.HudIntroSeen = true;
        many.Chrome.EnterHud();
        Settle(many);

        few.ClientSize.Height.Should().BeLessThan(many.ClientSize.Height);
        many.ClientSize.Height.Should().BeLessThanOrEqualTo(HudMetrics.MaxHeightFor(HudSize.Compact));
    }

    [Theory]
    [InlineData(HudSize.Compact)]
    [InlineData(HudSize.Normal)]
    [InlineData(HudSize.Expanded)]
    public void EachSize_HasItsOwnWidthAndCeiling(HudSize size)
    {
        HudMetrics.WidthFor(size).Should().BeInRange(240, 360);
        HudMetrics.MaxHeightFor(size).Should().BeGreaterThan(HudMetrics.MinHeight);
    }

    [AvaloniaFact]
    public async Task ChangingTheSize_InTheHud_AppliesRightAway()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.EnterHud();
        Settle(window);

        window.Chrome.UseHudSize("expanded");
        Settle(window);

        window.Width.Should().Be(HudMetrics.WidthFor(HudSize.Expanded));
        window.MaxHeight.Should().Be(HudMetrics.MaxHeightFor(HudSize.Expanded));
    }

    [AvaloniaFact]
    public async Task AnchoredBottomRight_ItSitsInThatCorner()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.UseHudPosition("BottomRight");
        window.Chrome.EnterHud();
        Settle(window);

        var area = WorkingArea(window);
        var size = PixelSizeOf(window);

        (window.Position.X + size.Width).Should().BeCloseTo(area.Right - Margin(window), 1);
        (window.Position.Y + size.Height).Should().BeCloseTo(area.Bottom - Margin(window), 1);
    }

    [AvaloniaFact]
    public async Task ChoosingAnotherCorner_InTheHud_MovesItRightAway()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.EnterHud();
        Settle(window);

        window.Chrome.UseHudPosition("TopRight");
        Settle(window);

        var area = WorkingArea(window);
        (window.Position.X + PixelSizeOf(window).Width).Should().BeCloseTo(area.Right - Margin(window), 1);
        window.Position.Y.Should().Be(area.Y + Margin(window));
    }

    [AvaloniaFact]
    public async Task ExitingTheHud_PutsTheWindowBackExactlyWhereItWas()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));

        window.Width = 420;
        window.Height = 500;
        window.Position = new PixelPoint(640, 220);
        Settle(window);

        window.Chrome.EnterHud();
        Settle(window);

        window.Position.Should().NotBe(new PixelPoint(640, 220));

        window.Chrome.ExitHud();
        Settle(window);

        window.Position.Should().Be(new PixelPoint(640, 220));
        window.Width.Should().Be(420);
        window.Height.Should().Be(500);
        window.CanResize.Should().BeTrue();
        window.Topmost.Should().BeFalse();
        WithClass(window, "titleBar").IsEffectivelyVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task TheHud_NeverOverwritesTheNormalWindowOnDisk()
    {
        var (window, store, _) = await ShowAsync(Row("Deploy"));

        window.Width = 420;
        Settle(window);
        window.PersistNow();
        var normal = store.Saved;

        window.Chrome.EnterHud();
        Settle(window);
        window.PersistNow();

        store.Saved.X.Should().Be(normal.X);
        store.Saved.Y.Should().Be(normal.Y);
        store.Saved.Width.Should().Be(normal.Width);
        store.Saved.Height.Should().Be(normal.Height);
        store.Saved.WindowMode.Should().Be(WindowMode.Hud);
    }

    [AvaloniaFact]
    public async Task TheHudButtons_TakeClicks_AndTheLitPinIsTheWayBack()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.EnterHud();
        Settle(window);

        var bar = WithClass(window, "hudBar");
        var buttons = bar.GetVisualDescendants().OfType<Button>().ToList();

        // Nova tarefa, histórico (ADR-053), sair do HUD, menu e fechar.
        buttons.Should().HaveCount(5).And.OnlyContain(button =>
            WindowDecorationProperties.GetElementRole(button) == WindowDecorationsElementRole.User);

        var exit = buttons.Single(button => button.Name == "HudExitButton");

        exit.Classes.Should().Contain("on");
        exit.Command.Should().BeSameAs(window.Chrome.ExitHudCommand);

        exit.Command!.Execute(null);
        Settle(window);

        window.Chrome.IsNormal.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task TheNormalHeaderPin_NowMeansFixAsHud()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));

        var pin = Named<Button>(window, button => button.Name == "HudButton");

        ToolTip.GetTip(pin).Should().Be("Fixar como HUD — fica visível sobre as outras janelas");
        pin.Command!.Execute(null);
        Settle(window);

        window.Chrome.IsHud.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task TheCollapsedHud_IsJustThePill()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"), Row("Revisar"));

        window.Chrome.EnterHud();
        window.Chrome.CollapseHud();
        Settle(window);

        window.Width.Should().Be(HudMetrics.CollapsedWidth);
        window.Height.Should().Be(HudMetrics.CollapsedHeight);

        Named<Button>(window, button => button.Classes.Contains("hudPill")).IsEffectivelyVisible.Should().BeTrue();
        window.GetVisualDescendants().OfType<TodayView>().Single().IsEffectivelyVisible.Should().BeFalse();
        VisibleTexts(window).Should().Contain("MyTaskApp").And.Contain("2");

        Named<Button>(window, button => button.Classes.Contains("hudPill")).Command!.Execute(null);
        Settle(window);

        window.Chrome.IsHudExpanded.Should().BeTrue();
        window.Width.Should().Be(HudMetrics.WidthFor(HudSize.Compact));
    }

    [AvaloniaFact]
    public async Task InTheHud_TheCompletedLeaveTheList_AndComeBackAfter()
    {
        // No HUD altura é o recurso escasso: o que já foi feito sai da lista.
        var (window, _, _) = await ShowAsync([Row("Deploy")], [Row("Revisar PR")]);

        VisibleTexts(window).Should().Contain("Revisar PR");

        window.Chrome.EnterHud();
        Settle(window);

        VisibleTexts(window).Should().NotContain("Revisar PR").And.Contain("Deploy");

        window.Chrome.ExitHud();
        Settle(window);

        VisibleTexts(window).Should().Contain("Revisar PR");
    }

    [AvaloniaFact]
    public async Task InTheHudWithEverythingDone_ItSaysSoInsteadOfGoingBlank()
    {
        var (window, _, _) = await ShowAsync([], [Row("Revisar PR")]);

        window.Chrome.EnterHud();
        Settle(window);

        VisibleTexts(window).Should().Contain("Tudo concluído. Aproveite.");
    }

    [AvaloniaFact]
    public async Task TheHudFromDisk_OpensAsTheHud_ShowingOnlyWhatIsPending()
    {
        var (window, _, _) = await ShowAsync(
            [Row("Deploy")],
            [Row("Revisar PR")],
            Placed with { WindowMode = WindowMode.Hud });

        window.Chrome.IsHud.Should().BeTrue();
        window.Topmost.Should().BeTrue();
        window.Width.Should().Be(HudMetrics.WidthFor(HudSize.Compact));
        VisibleTexts(window).Should().NotContain("Revisar PR").And.Contain("Deploy");
    }

    [AvaloniaFact]
    public async Task StartInHud_OpensAsTheHud()
    {
        var (window, _, _) = await ShowAsync([Row("Deploy")], null, Placed with { StartInHud = true });

        window.Chrome.IsHud.Should().BeTrue();
        WithClass(window, "hudBar").IsEffectivelyVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task WithoutKeepOnTop_TheHudIsAPlainWindow()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.HudAlwaysOnTop = false;
        window.Chrome.EnterHud();
        Settle(window);

        window.Chrome.IsHud.Should().BeTrue();
        window.Topmost.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task TheCapture_InTheHud_WaitsForThePlus()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.EnterHud();
        Settle(window);

        var card = Named<Border>(window, border => border.Name == "CaptureCard");

        card.IsEffectivelyVisible.Should().BeFalse();

        window.Chrome.ToggleCapture();
        Settle(window);

        card.IsEffectivelyVisible.Should().BeTrue();
        window.GetVisualDescendants().OfType<TextBox>().Single().IsFocused.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task TheNewTaskFooter_ShowsInTheLargerSizes()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));
        var footer = Named<Button>(window, button => button.Classes.Contains("hudFooter"));

        window.Chrome.EnterHud();
        Settle(window);

        footer.IsEffectivelyVisible.Should().BeFalse("no compacto o + do cabeçalho basta");

        window.Chrome.UseHudSize("normal");
        Settle(window);

        footer.IsEffectivelyVisible.Should().BeTrue();
        footer.Content.Should().Be("+ Nova tarefa");
    }

    [AvaloniaFact]
    public async Task TheIntro_ShowsTheFirstTimeAndIsRemembered()
    {
        var (window, store, _) = await ShowAsync(Row("Deploy"));

        window.Chrome.EnterHud();
        Settle(window);

        var intro = WithClass(window, "hudIntro");
        intro.IsEffectivelyVisible.Should().BeTrue();
        VisibleTexts(window).Should().Contain("Modo HUD");

        Named<Button>(intro, button => (button.Content as string) == "Entendi").Command!.Execute(null);
        Settle(window);

        intro.IsEffectivelyVisible.Should().BeFalse();

        window.PersistNow();
        store.Saved.Hud.IntroSeen.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task ClosingTheNormalWindow_WithCloseBehaviorHud_TurnsItIntoTheHud()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));
        window.Chrome.CloseBehavior = CloseBehavior.Hud;

        window.Close();
        Settle(window);

        window.IsVisible.Should().BeTrue();
        window.Chrome.IsHud.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task ClosingTheHud_WithCloseBehaviorHud_OffersTheWaysOut()
    {
        // Nunca simplesmente ignorar o clique: já no HUD, o X pergunta.
        var (window, _, _) = await ShowAsync(Row("Deploy"));
        window.Chrome.CloseBehavior = CloseBehavior.Hud;
        window.Chrome.TrayAvailable = true;
        window.Chrome.EnterHud();
        Settle(window);

        window.Chrome.RequestClose();
        Settle(window);

        window.IsVisible.Should().BeTrue();
        window.Chrome.IsHud.Should().BeTrue();
        window.AreCloseChoicesOpen.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task ClosingThePill_WithCloseBehaviorHud_OpensTheCardToAsk()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));
        window.Chrome.CloseBehavior = CloseBehavior.Hud;
        window.Chrome.EnterHud();
        window.Chrome.CollapseHud();
        Settle(window);

        window.Close();
        Settle(window);

        window.Chrome.IsHudExpanded.Should().BeTrue();
        window.AreCloseChoicesOpen.Should().BeTrue();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingWithTray_HidesFromAnyMode(bool inHud)
    {
        var (window, store, _) = await ShowAsync(Row("Deploy"));
        window.Chrome.TrayAvailable = true;

        if (inHud)
        {
            window.Chrome.EnterHud();
            Settle(window);
        }

        window.Close();
        Settle(window);

        window.IsVisible.Should().BeFalse();
        store.Saved.WindowMode.Should().Be(inHud ? WindowMode.Hud : WindowMode.Normal);
    }

    [AvaloniaFact]
    public async Task ClosingWithExit_GoesThroughTheAppsExit()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));
        var exits = 0;
        window.ExitHandler = () => exits++;
        window.Chrome.CloseBehavior = CloseBehavior.Exit;

        window.Close();
        Settle(window);

        exits.Should().Be(1);
    }

    [AvaloniaFact]
    public async Task LeaveTheAppFromTheHudMenu_GoesThroughTheAppsExit()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));
        var exits = 0;
        window.ExitHandler = () => exits++;
        window.Chrome.EnterHud();

        window.Chrome.RequestExit();

        exits.Should().Be(1);
    }

    [AvaloniaFact]
    public async Task WithoutTheApp_ExitSimplyClosesTheWindow()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Chrome.CloseBehavior = CloseBehavior.Exit;

        window.Close();
        Settle(window);

        closed.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task InTheHud_TheMouseBelongsToTheCardOnly_AndBackToTheWholeWindowAfter()
    {
        var (window, _, behavior) = await ShowAsync(Row("Deploy"));

        behavior.Region.Should().BeNull("a janela normal é inteira do mouse, como sempre foi");

        window.Chrome.EnterHud();
        Settle(window);

        behavior.Region.Should().Be(new Rect(window.ClientSize));
        behavior.Radius.Should().Be(HudMetrics.CornerRadius);

        window.Chrome.CollapseHud();
        Settle(window);

        behavior.Region.Should().Be(new Rect(window.ClientSize));
        behavior.Radius.Should().Be(HudMetrics.CollapsedHeight / 2);

        window.Chrome.ExitHud();
        Settle(window);

        behavior.Region.Should().BeNull();
    }

    [AvaloniaFact]
    public async Task TheClickRegion_IsNotDrivenByTopmostOrOpacity()
    {
        // HUD ≠ ClickThrough: a região é do formato da janela, e não de
        // preferência nenhuma.
        var (window, _, behavior) = await ShowAsync(Row("Deploy"));

        window.Chrome.EnterHud();
        Settle(window);
        var region = behavior.Region;

        window.Chrome.HudAlwaysOnTop = false;
        window.Chrome.HudOpacity = 0.75;
        Settle(window);

        behavior.Region.Should().Be(region);
        window.Chrome.ToggleTopmost();
        Settle(window);

        behavior.Region.Should().Be(region);
    }

    [AvaloniaFact]
    public async Task TheHudCheckboxInTheMenu_IsTheSameSettingAsTheSettingsWindow()
    {
        var (window, _, _) = await ShowAsync(Row("Deploy"));

        var settings = new WindowSettingsWindow(window.Chrome);
        settings.Show();
        settings.UpdateLayout();

        var keepOnTop = settings.GetVisualDescendants().OfType<CheckBox>()
            .Single(box => (box.Content as string)!.StartsWith("Manter o HUD", StringComparison.Ordinal));

        keepOnTop.IsChecked.Should().BeTrue();

        keepOnTop.IsChecked = false;

        window.Chrome.HudAlwaysOnTop.Should().BeFalse();

        settings.Close();
    }

    /// <summary>Lembra o que foi gravado, sem tocar no disco.</summary>
    internal sealed class RecordingStore : IWidgetStateStore
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

    /// <summary>Guarda o último recorte pedido; o de verdade é nativo e não roda no headless.</summary>
    internal sealed class RecordingBehavior : IWindowBehaviorService
    {
        public Rect? Region { get; private set; }

        public double Radius { get; private set; }

        public bool SupportsClickThrough => true;

        public void SetInteractiveRegion(Window window, Rect? region, double cornerRadius)
        {
            Region = region;
            Radius = cornerRadius;
        }
    }
}
