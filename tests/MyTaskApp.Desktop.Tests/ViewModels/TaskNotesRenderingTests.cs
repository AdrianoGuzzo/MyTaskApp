using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.Notes;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O que só quebra na tela: o ícone da linha, cujo comando passa por um caminho
/// com cast até o <c>DataContext</c> do <c>UserControl</c>, e a janela de
/// anotações, que troca o editor pelo leitor conforme a tarefa esteja aberta ou
/// concluída. Nenhum dos dois faz a build reclamar quando quebra.
/// </summary>
public class TaskNotesRenderingTests
{
    private static readonly DateOnly Date = new(2026, 9, 21);

    private static TodayTask Task(string title, string? notes = null) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            title,
            TaskPriority.Normal,
            Date,
            null,
            false,
            null,
            0,
            null,
            notes);

    private static async Task<MainWindow> ShowListAsync(TodayBoard board)
    {
        var viewModel = new TodayViewModel(
            new FakeUseCaseRunner { Result = board },
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

        await viewModel.LoadAsync(CancellationToken.None);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        return window;
    }

    private static Button NotesButton(Visual window) =>
        window.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Classes.Contains("notes"));

    // ------------------------------------------------------------------
    // O ícone na linha
    // ------------------------------------------------------------------

    /// <summary>
    /// O comando chega por <c>{Binding #Root.((vm:TodayViewModel)DataContext)…}</c>.
    /// Se esse caminho quebrar, o ícone continua desenhado e o clique não faz
    /// nada — em silêncio.
    /// </summary>
    [AvaloniaFact]
    public async Task EachRow_CarriesANotesButtonWiredToItsOwnRow()
    {
        var window = await ShowListAsync(
            new TodayBoard(Date, [], [], [Task("Fechar o mês")], [], []));

        var button = NotesButton(window);

        button.Command.Should().NotBeNull();
        button.CommandParameter.Should().BeOfType<TaskRowViewModel>();
    }

    /// <summary>
    /// O gesto é clique simples, e não duplo: o <c>Tapped</c> do título já copia
    /// o texto, e no Avalonia o primeiro clique de um duplo clique dispara o
    /// <c>Tapped</c> antes do <c>DoubleTapped</c>.
    /// </summary>
    [AvaloniaFact]
    public async Task ASingleClickOnTheIcon_AsksForTheNotes()
    {
        var window = await ShowListAsync(
            new TodayBoard(Date, [], [], [Task("Fechar o mês")], [], []));

        var viewModel = (TodayViewModel)window.DataContext!;
        TaskRowViewModel? asked = null;
        viewModel.NotesRequested += row => asked = row;

        var button = NotesButton(window);

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var centre = new Point(button.Bounds.Width / 2, button.Bounds.Height / 2);
        var point = button.TranslatePoint(centre, window)!.Value;

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        asked.Should().NotBeNull();
        asked!.Title.Should().Be("Fechar o mês");
    }

    /// <summary>
    /// Aceso sem hover é o indicador de "aqui tem mais coisa escrita". Sem ele,
    /// descobrir onde há anotação custaria abrir item por item.
    /// </summary>
    [AvaloniaFact]
    public async Task AnItemWithNotes_KeepsTheIconLit()
    {
        var window = await ShowListAsync(
            new TodayBoard(Date, [], [], [Task("Fechar o mês", "tem texto")], [], []));

        NotesButton(window).Classes.Should().Contain("on");
    }

    [AvaloniaFact]
    public async Task AnItemWithoutNotes_LeavesTheIconOff()
    {
        var window = await ShowListAsync(
            new TodayBoard(Date, [], [], [Task("Fechar o mês")], [], []));

        NotesButton(window).Classes.Should().NotContain("on");
    }

    /// <summary>
    /// O glifo é a única pista, antes do clique, de que a janela vai abrir só
    /// para ler.
    /// </summary>
    [AvaloniaFact]
    public async Task ACompletedItem_ShowsTheReadingGlyphInsteadOfThePencil()
    {
        var open = await ShowListAsync(
            new TodayBoard(Date, [], [], [Task("Fechar o mês")], [], []));

        var done = await ShowListAsync(
            new TodayBoard(Date, [], [], [], [], [Task("Fechar o mês")]));

        NotesButton(done).Content.Should().NotBe(NotesButton(open).Content);
    }

    // ------------------------------------------------------------------
    // A janela
    // ------------------------------------------------------------------

    private static TaskNotesWindow ShowNotes(TaskRowViewModel row)
    {
        var viewModel = new TaskNotesViewModel(
            new FakeUseCaseRunner(),
            new FakeDirectoryProbe(),
            TestDevelopment.For(new FakeUseCaseRunner()),
            NullLogger<TaskNotesViewModel>.Instance);

        viewModel.Load(row);

        var window = new TaskNotesWindow(viewModel, new FakeConfirmationDialog());

        window.Show();
        window.UpdateLayout();

        return window;
    }

    private static TaskRowViewModel Row(string? notes = null, bool isCompleted = false) =>
        new(Task("Fechar o mês", notes), isCompleted);

    [AvaloniaFact]
    public void AnOpenTask_GetsTheEditorAndTheToolbar()
    {
        var window = ShowNotes(Row("já escrito"));

        var editor = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "Editor");

        editor.IsVisible.Should().BeTrue();
        editor.Text.Should().Be("já escrito");

        window.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Classes.Contains("tool"))
            .Should().NotBeEmpty();
    }

    [AvaloniaFact]
    public void AnOpenTask_ShowsTheTitleReadyToEdit()
    {
        var window = ShowNotes(Row());

        var title = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "TitleEditor");

        title.IsEffectivelyVisible.Should().BeTrue();
        title.Text.Should().Be("Fechar o mês");
        title.MaxLength.Should().Be(TaskItem.MaxTitleLength);
    }

    /// <summary>Concluída, o título também vira registro — como a anotação.</summary>
    [AvaloniaFact]
    public void ACompletedTask_ShowsTheTitleButDoesNotLetItBeEdited()
    {
        var window = ShowNotes(Row(isCompleted: true));

        window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "TitleEditor")
            .IsVisible.Should().BeFalse();

        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Should().Contain(block => block.Text == "Fechar o mês" && block.IsEffectivelyVisible);
    }

    /// <summary>
    /// A regra do pedido. Note que a barra de formatação <b>some</b>, e não fica
    /// apagada: botão desabilitado ainda convida ao clique.
    /// </summary>
    [AvaloniaFact]
    public void ACompletedTask_GetsTheReaderAndNoToolbar()
    {
        var window = ShowNotes(Row("o que foi feito", isCompleted: true));

        window.GetVisualDescendants().OfType<TextBox>()
            .Should().AllSatisfy(editor => editor.IsVisible.Should().BeFalse());

        window.GetVisualDescendants().OfType<MarkdownView>()
            .Single().IsVisible.Should().BeTrue();

        // IsEffectivelyVisible, e não IsVisible: quem some é a barra inteira, e
        // um botão dentro de um pai escondido continua se declarando visível.
        window.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Classes.Contains("tool"))
            .Should().AllSatisfy(button => button.IsEffectivelyVisible.Should().BeFalse());
    }

    /// <summary>
    /// O leitor tem de desenhar formatação de verdade — senão o modo leitura
    /// mostraria os asteriscos do Markdown cru, que é pior do que texto simples.
    /// </summary>
    [AvaloniaFact]
    public void TheReader_DrawsTheFormattingInsteadOfTheMarkers()
    {
        var window = ShowNotes(Row("um **forte** aqui", isCompleted: true));

        var runs = window.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .SelectMany(block => block.Inlines ?? [])
            .OfType<Run>()
            .ToList();

        runs.Should().Contain(run => run.Text == "forte" && run.FontWeight == FontWeight.Bold);
        runs.Should().NotContain(run => run.Text!.Contains("**", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void ACompletedTaskWithoutNotes_SaysSoInsteadOfShowingNothing()
    {
        var window = ShowNotes(Row(isCompleted: true));

        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Should().Contain(block => block.Text == "Esta tarefa foi concluída sem nenhuma anotação.");
    }

    /// <summary>
    /// Cada botão da barra carrega no <c>Tag</c> o nome do comando que o
    /// code-behind vai interpretar. Um nome errado compila e não faz nada.
    /// </summary>
    [AvaloniaFact]
    public void EveryToolbarButton_NamesACommandThatExists()
    {
        var window = ShowNotes(Row());

        var tags = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Classes.Contains("tool"))
            .Select(button => button.Tag)
            .ToList();

        tags.Should().HaveCount(8);
        tags.Should().AllSatisfy(tag =>
            Enum.TryParse<MarkdownCommand>((string)tag!, out _).Should().BeTrue());
    }

    /// <summary>
    /// O clique no botão é um handler de code-behind ligado por nome no XAML:
    /// se o nome mudar de um lado só, a barra continua desenhada e não formata
    /// nada, sem a build reclamar.
    /// </summary>
    [AvaloniaFact]
    public void ClickingBold_WrapsWhatIsSelected()
    {
        var window = ShowNotes(Row("texto"));

        var editor = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "Editor");
        editor.SelectionStart = 0;
        editor.SelectionEnd = 5;

        var bold = window.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Classes.Contains("tool") && (string?)button.Tag == "Bold");

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var centre = new Point(bold.Bounds.Width / 2, bold.Bounds.Height / 2);
        var point = bold.TranslatePoint(centre, window)!.Value;

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        editor.Text.Should().Be("**texto**");
    }
}
