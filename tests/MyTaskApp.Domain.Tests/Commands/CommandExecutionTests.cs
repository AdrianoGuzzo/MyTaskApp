using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Domain.Tests.Commands;

/// <summary>O histórico de um comando rápido e o processo que ele abriu (ADR-051).</summary>
public class CommandExecutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    private static CommandExecution Execution(CommandMode mode = CommandMode.Execute, bool keepOpen = true) =>
        CommandExecution.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            null,
            "Testes",
            "dotnet test",
            @"C:\Projects\eco-feature-x",
            mode,
            keepOpen,
            Now);

    [Fact]
    public void Create_IsQueued_WithCopiesOfWhatRuns()
    {
        var execution = Execution();

        execution.Status.Should().Be(CommandExecutionStatus.Queued);
        execution.IsActive.Should().BeTrue();
        execution.CommandName.Should().Be("Testes");
        execution.CommandLine.Should().Be("dotnet test");
        execution.StartedAt.Should().Be(Now);
        execution.FinishedAt.Should().BeNull();
    }

    [Fact]
    public void KeepTerminalOpen_OnlyCountsInATerminal()
    {
        Execution(CommandMode.Execute, keepOpen: true).KeepTerminalOpen.Should().BeFalse();
        Execution(CommandMode.Terminal, keepOpen: true).KeepTerminalOpen.Should().BeTrue();
        Execution(CommandMode.Terminal, keepOpen: false).KeepTerminalOpen.Should().BeFalse();
    }

    [Fact]
    public void ExitZero_IsCompleted_WithTheOutput()
    {
        var execution = Execution();
        execution.MarkRunning();

        execution.Finish(0, "ok\n", "", Now.AddSeconds(3)).Should().BeTrue();

        execution.Status.Should().Be(CommandExecutionStatus.Completed);
        execution.ExitCode.Should().Be(0);
        execution.Output.Should().Be("ok\n");
        execution.ErrorOutput.Should().BeNull();
        execution.FinishedAt.Should().Be(Now.AddSeconds(3));
        execution.IsActive.Should().BeFalse();
    }

    [Fact]
    public void AnotherExitCode_IsFailed()
    {
        var execution = Execution();
        execution.MarkRunning();

        execution.Finish(1, null, "erro", Now);

        execution.Status.Should().Be(CommandExecutionStatus.Failed);
        execution.ExitCode.Should().Be(1);
        execution.ErrorOutput.Should().Be("erro");
    }

    [Fact]
    public void AClosedTerminal_WithoutExitCode_IsCompleted()
    {
        var execution = Execution(CommandMode.Terminal);
        execution.MarkRunning(4321, Now);

        execution.Finish(null, null, null, Now.AddMinutes(5));

        execution.Status.Should().Be(CommandExecutionStatus.Completed);
        execution.ExitCode.Should().BeNull();
        execution.ProcessId.Should().Be(4321);
    }

    [Fact]
    public void Cancelled_IsStopped_KeepingTheOutput()
    {
        var execution = Execution();
        execution.MarkRunning();

        execution.Stop("até aqui", null, Now);

        execution.Status.Should().Be(CommandExecutionStatus.Stopped);
        execution.Output.Should().Be("até aqui");
    }

    [Fact]
    public void Fail_KeepsTheReason()
    {
        var execution = Execution(CommandMode.Terminal);

        execution.Fail("Não foi possível abrir o terminal.", Now);

        execution.Status.Should().Be(CommandExecutionStatus.Failed);
        execution.FailureReason.Should().Be("Não foi possível abrir o terminal.");
    }

    [Fact]
    public void FinishingTwice_ChangesNothing()
    {
        var execution = Execution();
        execution.MarkRunning();
        execution.Finish(0, "ok", null, Now);

        execution.Finish(9, "outra", null, Now.AddHours(1)).Should().BeFalse();
        execution.Stop(null, null, Now).Should().BeFalse();
        execution.Fail("x", Now).Should().BeFalse();

        execution.Status.Should().Be(CommandExecutionStatus.Completed);
        execution.ExitCode.Should().Be(0);
    }

    [Fact]
    public void Starting_IsOnlyFromQueued()
    {
        var execution = Execution();
        execution.MarkRunning();

        var again = () => execution.MarkRunning(1, Now);

        again.Should().Throw<DomainException>();
    }

    [Fact]
    public void AnInvalidProcess_IsRejected()
    {
        var start = () => Execution(CommandMode.Terminal).MarkRunning(0, Now);

        start.Should().Throw<DomainException>();
    }

    [Fact]
    public void Output_KeepsTheTail_WithinTheLimit()
    {
        var text = new string('a', CommandExecution.MaxOutputLength) + "FIM";

        var tail = CommandExecution.Tail(text)!;

        tail.Length.Should().Be(CommandExecution.MaxOutputLength);
        tail.Should().StartWith("… (início omitido)").And.EndWith("FIM");
    }
}
