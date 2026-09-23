using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O autocomplete de diretórios na janela de anotação (ADR-026), com teclado de
/// verdade: o que só quebra na tela é a ordem dos handlers — o Enter que
/// quebraria a linha, o Escape que fecharia a janela e o Tab que tiraria o foco
/// precisam chegar à lista antes.
/// </summary>
public class TaskNotesAliasRenderingTests
{
    private static async Task<(TaskNotesWindow Window, TaskNotesViewModel ViewModel, FakeUseCaseRunner Runner)> ShowAsync()
    {
        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(GetTaskDirectoriesHandler)] = TaskNotesAliasTests.Directories;

        var viewModel = new TaskNotesViewModel(
            runner,
            new FakeDirectoryProbe(),
            TestDevelopment.For(runner),
            NullLogger<TaskNotesViewModel>.Instance);

        viewModel.Load(TaskNotesAliasTests.Row());

        var window = new TaskNotesWindow(viewModel, new FakeConfirmationDialog());
        window.Show();
        await viewModel.LoadAliasesAsync(CancellationToken.None);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        return (window, viewModel, runner);
    }

    private static TextBox Editor(Window window) =>
        window.GetVisualDescendants().OfType<TextBox>().Single();

    private static Popup AliasPopup(Window window) =>
        window.GetVisualDescendants().OfType<Popup>().Single(popup => popup.Name == "AliasPopup");

    private static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Write(Window window, string text)
    {
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task TypingTheAt_OpensTheListUnderTheText()
    {
        var (window, viewModel, _) = await ShowAsync();

        Write(window, "Verificar o código no @eco");

        viewModel.Completion.IsCompletionOpen.Should().BeTrue();
        AliasPopup(window).IsOpen.Should().BeTrue();
        AliasPopup(window).Child!.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(block => block.Text)
            .Should().Contain(["@ecossistema-core", @"C:\Projects\ecossistema-core", "ECO CORE"]);
        Editor(window).IsFocused.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task DownThenEnter_InsertsThePath_AndNotTheAlias()
    {
        var (window, viewModel, _) = await ShowAsync();
        Write(window, "Verificar o código no @eco");

        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Press(window, Key.Enter, PhysicalKey.Enter);

        Editor(window).Text.Should().Be(@"Verificar o código no C:\Projects\ecossistema-core");
        Editor(window).CaretIndex.Should().Be(Editor(window).Text!.Length);
        viewModel.Completion.IsCompletionOpen.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Tab_AlsoAccepts_AndTheFocusStaysInTheText()
    {
        var (window, _, _) = await ShowAsync();
        Write(window, "@mytask");

        Press(window, Key.Tab, PhysicalKey.Tab);

        Editor(window).Text.Should().Be(@"C:\Projetos\MyTaskApp");
        Editor(window).IsFocused.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Escape_ClosesOnlyTheList_AndKeepsTheTypedAt()
    {
        var (window, viewModel, _) = await ShowAsync();
        Write(window, "@eco");

        Press(window, Key.Escape, PhysicalKey.Escape);

        viewModel.Completion.IsCompletionOpen.Should().BeFalse();
        window.IsVisible.Should().BeTrue();
        Editor(window).Text.Should().Be("@eco");
    }

    /// <summary>
    /// A regressão do Popup: filtrar até não sobrar nada fecha a lista, e o
    /// <c>Closed</c> do Popup não pode tomar isso por "dispensou" — apagar a
    /// letra errada tem de trazer a lista de volta.
    /// </summary>
    [AvaloniaFact]
    public async Task NoMatch_ThenBackspace_ReopensTheList()
    {
        var (window, viewModel, _) = await ShowAsync();
        Write(window, "@ec");
        viewModel.Completion.IsCompletionOpen.Should().BeTrue();

        Write(window, "x");
        viewModel.Completion.IsCompletionOpen.Should().BeFalse();

        Press(window, Key.Back, PhysicalKey.Backspace);

        viewModel.Completion.IsCompletionOpen.Should().BeTrue();
        AliasPopup(window).IsOpen.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task ClickingAnItem_InsertsItsPath()
    {
        var (window, _, _) = await ShowAsync();
        Write(window, "ver @ecossistema");

        var item = AliasPopup(window).Child!.GetVisualDescendants()
            .OfType<Border>()
            .First(border => border.Classes.Contains("aliasItem")
                && border.DataContext is AliasSuggestionViewModel { Alias: "@ecossistema-web" });

        var root = TopLevel.GetTopLevel(item)!;
        var center = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), root)!.Value;
        root.MouseDown(center, MouseButton.Left);
        root.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Editor(window).Text.Should().Be(@"ver C:\Projects\ecossistema-web");
    }

    /// <summary>
    /// O que vai para o banco é texto puro: o path que foi inserido, sem marca
    /// nenhuma do alias. Mudar o diretório depois não tem o que reescrever.
    /// </summary>
    [AvaloniaFact]
    public async Task Saving_StoresThePathAsPlainText()
    {
        var (window, viewModel, runner) = await ShowAsync();
        Write(window, "Verificar @ecossistema-c");
        Press(window, Key.Enter, PhysicalKey.Enter);

        await viewModel.SaveAsync(CancellationToken.None);

        runner.Invoked.Should().Contain(typeof(UpdateTaskHandler));
        viewModel.Text.Should().Be(@"Verificar C:\Projects\ecossistema-core");
    }
}
