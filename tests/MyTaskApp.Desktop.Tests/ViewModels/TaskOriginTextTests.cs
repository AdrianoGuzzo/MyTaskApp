using MyTaskApp.Application.Sync;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>A linha "Criada em … · alterada por último em …" no cabeçalho da tarefa (ADR-061).</summary>
public class TaskOriginTextTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 12, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ATaskThatNeverSynced_HasNoLine() =>
        TaskNotesViewModel.DescribeOrigin(null).Should().BeNull();

    [Fact]
    public void ATaskChangedOnAnotherDevice_SaysBoth()
    {
        var text = TaskNotesViewModel.DescribeOrigin(new TaskOriginView("Notebook", Created, GetTaskOriginHandler.ThisComputer, Created));

        text.Should().StartWith("Criada em Notebook · ")
            .And.Contain(Created.ToLocalTime().ToString("dd/MM/yyyy HH:mm"))
            .And.EndWith(" · alterada por último neste computador");
    }

    [Fact]
    public void ATaskOnlyTouchedWhereItWasCreated_SaysItOnce() =>
        TaskNotesViewModel.DescribeOrigin(new TaskOriginView("Notebook", null, "Notebook", null))
            .Should().Be("Criada em Notebook");
}
