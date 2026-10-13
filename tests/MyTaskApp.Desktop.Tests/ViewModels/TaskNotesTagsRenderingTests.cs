using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// As etiquetas no cabeçalho da janela da tarefa (ADR-025). O "×" das pílulas e
/// as caixas do seletor chegam aos comandos por <c>#Root</c>, de dentro de um
/// template e de um popup: se o caminho quebrar, a tela desenha e o clique não
/// grava nada — em silêncio.
/// </summary>
public class TaskNotesTagsRenderingTests
{
    private static readonly IReadOnlyList<TagRow> AllTags =
    [
        new(Guid.NewGuid(), "Financeiro", "#3B82F6", 0),
        new(Guid.NewGuid(), "Urgente", "#EF4444", 0),
    ];

    private static TodayTask Task(params TagBadge[] tags) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "Pagar boleto",
            TaskPriority.Normal,
            new DateOnly(2026, 10, 13),
            null,
            false,
            Tags: tags);

    private static TaskNotesWindow Show(TodayTask task, bool isCompleted = false)
    {
        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(GetTagsHandler)] = AllTags;

        var notes = new TaskNotesViewModel(
            runner,
            new FakeDirectoryProbe(),
            TestDevelopment.For(new FakeUseCaseRunner()),
            NullLogger<TaskNotesViewModel>.Instance,
            tags: new TaskTagEditorViewModel(runner, NullLogger<TaskTagEditorViewModel>.Instance));

        notes.Load(new TaskRowViewModel(task, isCompleted));

        var window = new TaskNotesWindow(notes, new FakeConfirmationDialog());
        window.Show();
        window.UpdateLayout();

        return window;
    }

    private static ContentControl Section(Window window) =>
        window.GetVisualDescendants().OfType<ContentControl>().Single(control => control.Name == "TagSection");

    private static Button PickerButton(Window window) =>
        Section(window).GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("addTag"));

    private static List<Button> Crosses(Window window) =>
        [.. Section(window).GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("tagRemove"))];

    [AvaloniaFact]
    public void TheHeader_ShowsEachTagByName_WithACrossWiredToRemoveIt()
    {
        var window = Show(Task(new TagBadge(AllTags[1].Id, "Urgente", "#EF4444")));

        var names = Section(window).GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        var cross = Crosses(window).Single();

        Section(window).IsVisible.Should().BeTrue();
        names.Should().Contain("Urgente");
        cross.IsVisible.Should().BeTrue();
        cross.Command.Should().NotBeNull();
        cross.CommandParameter.Should().BeOfType<TagChipViewModel>();
        PickerButton(window).IsVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task ThePicker_OpensWithEveryTagWiredToTheCommands()
    {
        var window = Show(Task());
        var button = PickerButton(window);

        button.Flyout!.ShowAt(button);
        await System.Threading.Tasks.Task.Yield();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var popup = ((Flyout)button.Flyout).Content.Should().BeAssignableTo<Control>().Subject;
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

    /// <summary>Concluída, a janela é registro: as pílulas ficam, sem "×" nem seletor.</summary>
    [AvaloniaFact]
    public void ACompletedTask_ShowsThePills_WithoutWaysToChangeThem()
    {
        var window = Show(Task(new TagBadge(AllTags[0].Id, "Financeiro", "#3B82F6")), isCompleted: true);

        Section(window).IsVisible.Should().BeTrue();
        Crosses(window).Should().OnlyContain(cross => !cross.IsVisible);
        PickerButton(window).IsVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public void ACompletedTaskWithoutTags_HasNoTagSection()
    {
        var window = Show(Task(), isCompleted: true);

        Section(window).IsVisible.Should().BeFalse();
    }
}
