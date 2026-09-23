using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O que só quebra na tela (ADR-025): bolinha sem cor, balão sem nome, o "+N"
/// e a janela de etiquetas, que depende do tema do ColorView carregado.
/// </summary>
public class TagRenderingTests
{
    private static readonly DateOnly Date = new(2026, 9, 22);

    private static TodayTask Task(params TagBadge[] tags) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "Pagar boleto",
            TaskPriority.Normal,
            Date,
            null,
            false,
            Tags: tags);

    private static readonly IReadOnlyList<TagRow> AllTags =
    [
        new(Guid.NewGuid(), "Financeiro", "#3B82F6", 0),
        new(Guid.NewGuid(), "Urgente", "#EF4444", 0),
    ];

    private static async Task<MainWindow> ShowListAsync(TodayTask task)
    {
        var runner = new FakeUseCaseRunner { Result = new TodayBoard(Date, [], [], [task], [], []) };
        runner.ResultsByHandler[typeof(GetTagsHandler)] = AllTags;

        var viewModel = new TodayViewModel(
            runner,
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

    private static List<Ellipse> Dots(Window window) =>
        [.. window.GetVisualDescendants().OfType<Ellipse>().Where(dot => dot.Classes.Contains("tagDot"))];

    [AvaloniaFact]
    public async Task EachTag_IsADotInItsOwnColor_NamedByTheTooltip()
    {
        var window = await ShowListAsync(Task(
            new TagBadge(Guid.NewGuid(), "Urgente", "#EF4444"),
            new TagBadge(Guid.NewGuid(), "Financeiro", "#3B82F6")));

        var dots = Dots(window);

        dots.Select(dot => ((ISolidColorBrush)dot.Fill!).Color)
            .Should().Equal(Color.Parse("#3B82F6"), Color.Parse("#EF4444"));
        dots.Select(dot => OpenTip(dot).Content).Should().Equal("Financeiro", "Urgente");
    }

    /// <summary>
    /// O balão é um ToolTip explícito, e as amarrações dele só resolvem se o
    /// DataContext da bolinha chegar até lá — o que só se vê com ele aberto.
    /// </summary>
    [AvaloniaFact]
    public async Task TheTooltip_IsPaintedInTheTagsColor_WithReadableText()
    {
        var window = await ShowListAsync(Task(
            new TagBadge(Guid.NewGuid(), "Urgente", "#B91C1C"),
            new TagBadge(Guid.NewGuid(), "Pendência", "#EAB308")));

        var dots = Dots(window);

        var yellow = OpenTip(dots[0]);
        yellow.Content.Should().Be("Pendência");
        ((ISolidColorBrush)yellow.Background!).Color.Should().Be(Color.Parse("#EAB308"));
        ((ISolidColorBrush)yellow.Foreground!).Color.Should().NotBe(Colors.White);
        yellow.Classes.Should().Contain("tagTip");

        var red = OpenTip(dots[1]);
        ((ISolidColorBrush)red.Background!).Color.Should().Be(Color.Parse("#B91C1C"));
        ((ISolidColorBrush)red.Foreground!).Color.Should().Be(Colors.White);
    }

    /// <summary>
    /// Acima e colado na bolinha. O deslocamento padrão do ToolTip (20px para
    /// baixo) jogaria o balão de volta por cima dela.
    /// </summary>
    [AvaloniaFact]
    public async Task TheTooltip_SitsRightAboveTheDot()
    {
        var window = await ShowListAsync(Task(new TagBadge(Guid.NewGuid(), "Urgente", "#EF4444")));

        var dot = Dots(window).Single();

        ToolTip.GetPlacement(dot).Should().Be(PlacementMode.Top);
        ToolTip.GetVerticalOffset(dot).Should().BeLessThanOrEqualTo(0);
    }

    /// <summary>
    /// O seletor é um template compartilhado, desenhado dentro de um popup: os
    /// comandos chegam por #Root, e se esse caminho quebrar a caixa marca e
    /// nada é gravado — em silêncio. Só com o flyout aberto dá para ver.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("tags")]
    [InlineData("captureTags")]
    public async Task BothPickers_OpenWithEveryTagWiredToTheCommands(string buttonClass)
    {
        var window = await ShowListAsync(Task());

        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(candidate => candidate.Classes.Contains(buttonClass));

        button.Flyout!.ShowAt(button);
        await System.Threading.Tasks.Task.Yield();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var popup = ((Flyout)button.Flyout).Content.Should().BeOfType<ContentControl>().Subject;
        popup.UpdateLayout();

        var options = popup.GetVisualDescendants().OfType<CheckBox>()
            .Where(box => box.Classes.Contains("tagOption"))
            .ToList();

        options.Should().HaveCount(AllTags.Count);
        options.Should().OnlyContain(box => box.Command != null && box.CommandParameter is TagOptionViewModel);

        popup.GetVisualDescendants().OfType<Button>()
            .Single(candidate => candidate.Classes.Contains("tagManage"))
            .Command.Should().NotBeNull();

        button.Flyout.Hide();
    }

    [AvaloniaFact]
    public async Task TheCaptureBox_ShowsTheChosenTagsAsDots()
    {
        var window = await ShowListAsync(Task());
        var viewModel = (TodayViewModel)window.DataContext!;

        await viewModel.OpenTagPickerAsync(viewModel.CaptureTags, CancellationToken.None);
        await viewModel.ToggleTagAsync(viewModel.CaptureTags.Options[1], CancellationToken.None);
        window.UpdateLayout();

        var dot = Dots(window).Should().ContainSingle().Subject;
        OpenTip(dot).Content.Should().Be("Urgente");

        window.GetVisualDescendants().OfType<Button>()
            .Single(candidate => candidate.Classes.Contains("captureTags"))
            .Classes.Should().Contain("on");
    }

    /// <summary>
    /// O balão do título traz, embaixo do texto, todas as etiquetas com nome e
    /// cor — inclusive as que o "+N" escondeu da linha.
    /// </summary>
    [AvaloniaFact]
    public async Task TheTitleTooltip_ListsEveryTagInItsOwnColor()
    {
        var tags = Enumerable.Range(1, 6)
            .Select(index => new TagBadge(Guid.NewGuid(), $"Etiqueta {index}", "#94A3B8"))
            .Append(new TagBadge(Guid.NewGuid(), "Urgente", "#B91C1C"))
            .ToArray();

        var window = await ShowListAsync(Task(tags));

        var title = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => block.Classes.Contains("taskTitle"));

        ToolTip.SetIsOpen(title, true);
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var tip = ToolTip.GetTip(title).Should().BeOfType<ToolTip>().Subject;
        var pills = tip.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("tipTag"))
            .ToList();

        pills.Should().HaveCount(7);

        var urgent = pills.Single(pill => ((TextBlock)pill.Child!).Text == "Urgente");
        ((ISolidColorBrush)urgent.Background!).Color.Should().Be(Color.Parse("#B91C1C"));
        ((ISolidColorBrush)((TextBlock)urgent.Child!).Foreground!).Color.Should().Be(Colors.White);

        tip.GetVisualDescendants().OfType<TextBlock>()
            .Should().Contain(block => block.Text == "Pagar boleto");

        ToolTip.SetIsOpen(title, false);
    }

    private static ToolTip OpenTip(Ellipse dot)
    {
        ToolTip.SetIsOpen(dot, true);
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var tip = ToolTip.GetTip(dot).Should().BeOfType<ToolTip>().Subject;
        ToolTip.SetIsOpen(dot, false);

        return tip;
    }

    [AvaloniaFact]
    public async Task ManyTags_StopAtFiveDotsAndACounter()
    {
        var tags = Enumerable.Range(1, 8)
            .Select(index => new TagBadge(Guid.NewGuid(), $"Etiqueta {index}", "#94A3B8"))
            .ToArray();

        var window = await ShowListAsync(Task(tags));

        Dots(window).Should().HaveCount(TaskTagsViewModel.MaxDots);

        // O da caixa de captura também existe, escondido enquanto nada foi escolhido.
        var counter = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Classes.Contains("tagOverflow") && text.IsVisible);
        counter.Text.Should().Be("+3");
    }

    [AvaloniaFact]
    public async Task ARowWithoutTags_DrawsNoDotsAndLeavesTheButtonOff()
    {
        var window = await ShowListAsync(Task());

        Dots(window).Should().BeEmpty();

        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(candidate => candidate.Classes.Contains("tags"));
        button.Classes.Should().NotContain("on");
        button.Flyout.Should().NotBeNull();
    }

    [AvaloniaFact]
    public async Task ATaggedRow_KeepsTheButtonLit()
    {
        var window = await ShowListAsync(Task(new TagBadge(Guid.NewGuid(), "Urgente", "#EF4444")));

        window.GetVisualDescendants().OfType<Button>()
            .Single(candidate => candidate.Classes.Contains("tags"))
            .Classes.Should().Contain("on");
    }

    [AvaloniaFact]
    public async Task TheTagsWindow_DrawsThePaletteWiredToThePicker()
    {
        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(GetTagsHandler)] =
            (IReadOnlyList<TagRow>)[new TagRow(Guid.NewGuid(), "Urgente", "#EF4444", 2)];

        var viewModel = new TagsViewModel(
            runner, new FakeConfirmationDialog(), new FakeDirectoryProbe(), NullLogger<TagsViewModel>.Instance);

        var window = new TagsWindow(viewModel);
        window.Show();
        window.Reveal();
        await System.Threading.Tasks.Task.Yield();
        window.UpdateLayout();

        var swatches = window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("swatch"))
            .ToList();

        swatches.Should().HaveCount(TagColor.Palette.Count);
        swatches.Should().OnlyContain(button => button.Command != null);

        swatches[2].Command!.Execute(swatches[2].CommandParameter);
        viewModel.ColorHex.Should().Be(TagColor.Palette[2]);

        window.GetVisualDescendants().OfType<TextBlock>()
            .Should().Contain(text => text.Text == "Usada em 2 tarefas");
    }
}
