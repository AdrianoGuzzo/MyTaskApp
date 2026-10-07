using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Desktop.Tests.ViewModels;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;

namespace MyTaskApp.Desktop.Tests.StickyNotes;

/// <summary>
/// A janela do post-it, de verdade, no host headless (ADR-054). O que estes
/// testes guardam são os fios que se rompem sem quebrar a build: papéis de
/// arrasto, o pino só como ordem Z, e o X que fecha sem excluir.
/// </summary>
public class StickyNoteWindowTests
{
    private readonly FakeUseCaseRunner _runner = new();

    public StickyNoteWindowTests() =>
        _runner.ResultsByHandler[typeof(SetStickyNoteOpenHandler)] = new SetStickyNoteOpenResult(Discarded: false);

    private StickyNoteWindow Show(StickyNoteView view)
    {
        var viewModel = new StickyNoteViewModel(view, _runner, NullLogger.Instance);
        var window = new StickyNoteWindow(viewModel, anchor: null, cascadeIndex: 0);

        window.Show();
        Settle(window);

        return window;
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static T Named<T>(Visual window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    [AvaloniaFact]
    public void TheNote_DrawsItsTextAndHeader()
    {
        var window = Show(TestStickyNotes.View("Perguntar ao João\nsobre a API"));

        Named<TextBox>(window, "Editor").Text.Should().Be("Perguntar ao João\nsobre a API");
        window.Title.Should().Be("Post-it — Perguntar ao João");
        window.ShowInTaskbar.Should().BeFalse();
        window.GetVisualDescendants().OfType<TextBlock>()
            .Should().Contain(block => block.Text == "Perguntar ao João");
    }

    /// <summary>
    /// O cabeçalho é a barra de arrasto; os botões dentro dele precisam de
    /// <c>User</c>, senão o clique vira arrasto (armadilha nº 4 do ADR-017). O
    /// headless não faz hit-test não-cliente, então afirma-se o papel declarado.
    /// </summary>
    [AvaloniaFact]
    public void TheHeaderDrags_ButItsButtonsReceiveTheClick()
    {
        var window = Show(TestStickyNotes.View("algo"));

        WindowDecorationProperties.GetElementRole(Named<Border>(window, "Header"))
            .Should().Be(WindowDecorationsElementRole.TitleBar);

        foreach (var name in new[] { "PinButton", "MenuButton", "CloseButton" })
        {
            WindowDecorationProperties.GetElementRole(Named<Button>(window, name))
                .Should().Be(WindowDecorationsElementRole.User, name);
        }

        // O texto não é área de arrasto: selecionar com o mouse não move a janela.
        Named<TextBox>(window, "Editor").GetSelfAndVisualAncestors()
            .Select(WindowDecorationProperties.GetElementRole)
            .Should().NotContain(WindowDecorationsElementRole.TitleBar);
    }

    [AvaloniaFact]
    public void TheNote_IsResizableFromEveryEdge()
    {
        var window = Show(TestStickyNotes.View("algo"));

        var roles = window.GetVisualDescendants()
            .OfType<Border>()
            .Select(WindowDecorationProperties.GetElementRole)
            .Where(role => role.ToString().StartsWith("Resize", StringComparison.Ordinal))
            .ToList();

        roles.Should().HaveCount(8).And.OnlyHaveUniqueItems();
        window.CanResize.Should().BeTrue();
        window.MinWidth.Should().Be(200);
        window.MinHeight.Should().Be(120);
    }

    [AvaloniaFact]
    public void ANoteWithAPlace_OpensWhereItWas()
    {
        var window = Show(TestStickyNotes.View("algo", x: 400, y: 300, width: 320, height: 240));

        window.Position.Should().Be(new PixelPoint(400, 300));
        window.Width.Should().Be(320);
        window.Height.Should().Be(240);
    }

    [AvaloniaFact]
    public void ANewNote_LandsInsideTheScreen()
    {
        var window = Show(TestStickyNotes.View());
        var area = window.Screens.Primary!.WorkingArea;

        area.Contains(new PixelRect(window.Position, new PixelSize((int)window.Width, (int)window.Height)))
            .Should().BeTrue();
    }

    /// <summary>"Fixar" é ordem Z e nada além disso — a mesma promessa do WidgetPinTests.</summary>
    [AvaloniaFact]
    public async Task Pinning_PutsItOnTopWithoutTouchingTheGeometry()
    {
        var window = Show(TestStickyNotes.View("algo", x: 400, y: 300));
        var before = (window.Position, window.Width, window.Height, window.CanResize);

        await window.ViewModel!.TogglePinCommand.ExecuteAsync(null);
        Settle(window);

        window.Topmost.Should().BeTrue();
        (window.Position, window.Width, window.Height, window.CanResize).Should().Be(before);

        await window.ViewModel.TogglePinCommand.ExecuteAsync(null);
        Settle(window);

        window.Topmost.Should().BeFalse();
    }

    [AvaloniaFact]
    public void APinnedNote_OpensOnTop()
    {
        var window = Show(TestStickyNotes.View("algo", pinned: true));

        window.Topmost.Should().BeTrue();
        Named<Button>(window, "PinButton").Classes.Should().Contain("on");
    }

    [AvaloniaFact]
    public async Task TheX_ClosesTheWindowWithoutDeleting()
    {
        var window = Show(TestStickyNotes.View("algo"));
        var closed = false;
        window.Closed += (_, _) => closed = true;

        await window.ViewModel!.CloseCommand.ExecuteAsync(null);
        Settle(window);

        closed.Should().BeTrue();
        _runner.Invoked.Should().Contain(typeof(SetStickyNoteOpenHandler))
            .And.NotContain(typeof(PurgeStickyNoteHandler))
            .And.NotContain(typeof(MoveStickyNoteToTrashHandler));
    }

    /// <summary>Alt+F4 e o "fechar" do sistema passam pelo mesmo caminho do X.</summary>
    [AvaloniaFact]
    public void ClosingTheWindowDirectly_GoesThroughTheSameSave()
    {
        var window = Show(TestStickyNotes.View("algo"));
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.ViewModel!.Content = "última palavra";

        window.Close();
        Settle(window);

        // O lugar em que o post-it novo pousou também vai junto.
        _runner.Invoked.Should().Equal(
            typeof(EditStickyNoteHandler),
            typeof(PlaceStickyNoteHandler),
            typeof(SetStickyNoteOpenHandler));
        closed.Should().BeTrue();
    }

    [AvaloniaFact]
    public void Escape_ClosesTheNote()
    {
        var window = Show(TestStickyNotes.View("algo"));
        window.FocusEditor();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(window);

        _runner.Invoked.Should().Contain(typeof(SetStickyNoteOpenHandler));
    }

    [AvaloniaFact]
    public void Typing_WaitsForAPauseBeforeSaving()
    {
        var window = Show(TestStickyNotes.View());
        window.FocusEditor();

        window.KeyTextInput("Ideia");
        Settle(window);

        window.ViewModel!.Content.Should().Be("Ideia");
        window.IsContentSavePending.Should().BeTrue();
        _runner.Invoked.Should().NotContain(typeof(EditStickyNoteHandler));
    }

    [AvaloniaFact]
    public void OpeningANote_PutsTheCursorInTheText()
    {
        var window = Show(TestStickyNotes.View("começo"));

        window.FocusEditor();

        var editor = Named<TextBox>(window, "Editor");
        editor.IsFocused.Should().BeTrue();
        editor.CaretIndex.Should().Be("começo".Length);
    }

    [AvaloniaFact]
    public void MovingTheWindow_IsSavedAfterThePause()
    {
        var window = Show(TestStickyNotes.View("algo", x: 400, y: 300));

        window.Position = new PixelPoint(420, 330);
        Settle(window);

        window.IsGeometrySavePending.Should().BeTrue();
    }

    [AvaloniaFact]
    public void TheControls_StayHiddenUntilHoverOrFocus()
    {
        var window = Show(TestStickyNotes.View("algo"));
        var close = Named<Button>(window, "CloseButton");

        close.Opacity.Should().Be(0);

        close.Focus();
        Settle(window);

        // O valor de base: a opacidade tem transição, e o efetivo estaria no meio dela.
        close.GetBaseValue(Visual.OpacityProperty).GetValueOrDefault().Should().Be(1d);
    }
}
