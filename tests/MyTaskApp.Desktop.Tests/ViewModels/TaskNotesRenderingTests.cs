using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
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
    public async Task ACompletedItem_ShowsTheReadingGlyphInsteadOfTheOpenOne()
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

    // ------------------------------------------------------------------
    // Pré-visualização enquanto escreve
    // ------------------------------------------------------------------

    private static TextBox Editor(Visual window) =>
        window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "Editor");

    // Pelo nome, e não pela árvore visual: escondido, o leitor nem entra nela.
    private static MarkdownView Preview(TaskNotesWindow window) =>
        window.FindControl<MarkdownView>("Preview")!;

    /// <summary>
    /// Pela árvore lógica: fora da árvore visual, <c>IsEffectivelyVisible</c>
    /// não tem pai para consultar e responde sempre que sim.
    /// </summary>
    private static bool IsShown(Control control) =>
        control.GetLogicalAncestors().OfType<Control>().Prepend(control).All(item => item.IsVisible);

    /// <summary>Abrir para escrever continua como era: só a caixa de texto.</summary>
    [AvaloniaFact]
    public void AnOpenTask_StartsWritingWithThePreviewHidden()
    {
        var window = ShowNotes(Row("um **forte**"));

        Editor(window).IsEffectivelyVisible.Should().BeTrue();
        IsShown(Preview(window)).Should().BeFalse();
    }

    [AvaloniaFact]
    public void PreviewMode_SwapsTheEditorForTheFormattedText()
    {
        var window = ShowNotes(Row("um **forte**"));
        var viewModel = (TaskNotesViewModel)window.DataContext!;

        viewModel.SetViewModeCommand.Execute(NotesViewMode.Preview);
        window.UpdateLayout();

        Editor(window).IsEffectivelyVisible.Should().BeFalse();
        IsShown(Preview(window)).Should().BeTrue();

        // Sem seleção para formatar, a barra de formatação sai junto.
        window.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Classes.Contains("tool"))
            .Should().AllSatisfy(button => button.IsEffectivelyVisible.Should().BeFalse());
    }

    /// <summary>
    /// Lado a lado, a pré-visualização acompanha cada tecla — é para isso que
    /// ela existe — e as duas folhas dividem a largura.
    /// </summary>
    [AvaloniaFact]
    public void SplitMode_ShowsBothAndFollowsTheTyping()
    {
        var window = ShowNotes(Row("antes"));
        var viewModel = (TaskNotesViewModel)window.DataContext!;

        viewModel.SetViewModeCommand.Execute(NotesViewMode.Split);
        window.UpdateLayout();

        var editor = Editor(window);
        var preview = Preview(window);

        editor.IsEffectivelyVisible.Should().BeTrue();
        IsShown(preview).Should().BeTrue();

        editor.Text = "# Depois";
        window.UpdateLayout();

        preview.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .SelectMany(block => block.Inlines ?? [])
            .OfType<Run>()
            .Should().Contain(run => run.Text == "Depois");

        var editorSheet = editor.FindAncestorOfType<Border>()!;
        var previewSheet = preview.FindAncestorOfType<ScrollViewer>()!.FindAncestorOfType<Border>()!;

        previewSheet.Bounds.Left.Should().BeGreaterThan(editorSheet.Bounds.Right - 1);
    }

    [AvaloniaFact]
    public void CtrlShiftV_TogglesThePreviewLikeVsCode()
    {
        var window = ShowNotes(Row("texto"));
        var viewModel = (TaskNotesViewModel)window.DataContext!;

        Editor(window).Focus();

        window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control | RawInputModifiers.Shift);
        viewModel.ViewMode.Should().Be(NotesViewMode.Preview);

        window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control | RawInputModifiers.Shift);
        viewModel.ViewMode.Should().Be(NotesViewMode.Write);

        // O atalho não pode ter colado nada na anotação no caminho.
        Editor(window).Text.Should().Be("texto");
    }

    [AvaloniaFact]
    public void ACompletedTask_HasNoModeSwitch()
    {
        var window = ShowNotes(Row("feito", isCompleted: true));

        window.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Classes.Contains("mode"))
            .Should().AllSatisfy(button => button.IsEffectivelyVisible.Should().BeFalse());
    }

    /// <summary>
    /// O que o leitor antigo mostrava com os marcadores à mostra, ou achatado
    /// num parágrafo: agora cada um vira o controle certo.
    /// </summary>
    [AvaloniaFact]
    public void TheReader_DrawsCodeTablesAndQuotes()
    {
        var window = ShowNotes(Row(
            "### Passos\n\n> cuidado\n\n```\ngit status\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\nrode `dotnet test`",
            isCompleted: true));

        var runs = window.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .SelectMany(block => block.Inlines ?? [])
            .OfType<Run>()
            .ToList();

        runs.Should().Contain(run => run.Text == "Passos");
        runs.Should().Contain(run => run.Text == "cuidado");
        runs.Should().Contain(run => run.Text == "dotnet test" && run.FontFamily.Name.Contains("Mono"));
        runs.Should().NotContain(run => run.Text!.IndexOfAny(new[] { '#', '`', '|', '>' }) >= 0);

        window.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .Should().Contain(block => block.Text == "git status" && block.TextWrapping == TextWrapping.NoWrap);

        window.GetVisualDescendants()
            .OfType<Grid>()
            .Should().Contain(grid => grid.ColumnDefinitions.Count == 2 && grid.RowDefinitions.Count == 2);
    }

    /// <summary>
    /// O clique no link é resolvido pela posição no texto do bloco, contada à
    /// mão. Se a quebra de linha tiver outro tamanho no <c>TextLayout</c>, todo
    /// link depois de uma quebra abre o vizinho — ou nada.
    /// </summary>
    [AvaloniaFact]
    public void ALinkAfterALineBreak_IsFoundWhereTheLayoutPutsIt()
    {
        var window = ShowNotes(Row("primeira linha\n[o link](https://exemplo.com)", isCompleted: true));
        var preview = Preview(window);

        var block = preview.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
        var secondLine = block.TextLayout.TextLines[1].FirstTextSourceIndex;

        preview.LinkAt(block, secondLine).Should().Be("https://exemplo.com");
        preview.LinkAt(block, secondLine - 1).Should().BeNull();
    }

    [AvaloniaFact]
    public void OnlyWebAndMailLinks_AreOpened()
    {
        var window = ShowNotes(Row("x", isCompleted: true));
        var preview = Preview(window);
        var opened = new List<Uri>();
        preview.LinkLauncher = uri =>
        {
            opened.Add(uri);
            return System.Threading.Tasks.Task.CompletedTask;
        };

        preview.OpenLink("https://exemplo.com");
        preview.OpenLink("mailto:alguem@exemplo.com");
        preview.OpenLink("file:///C:/Windows/System32/calc.exe");
        preview.OpenLink("C:\\Windows\\notepad.exe");

        opened.Select(uri => uri.Scheme).Should().Equal("https", "mailto");
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
