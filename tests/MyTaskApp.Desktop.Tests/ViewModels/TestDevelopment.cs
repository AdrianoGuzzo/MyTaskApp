using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A aba Desenvolvimento com dublês, para os testes da anotação que não se
/// interessam por ela (ADR-027) — e um ambiente só, para os que se interessam
/// pelo painel de um repositório (ADR-031).
/// </summary>
internal static class TestDevelopment
{
    /// <summary>A aba inteira: as abas de repositório e o painel do escolhido.</summary>
    public static TaskDevelopmentsViewModel For(
        FakeUseCaseRunner runner,
        FakeClipboardWriter? clipboard = null,
        FakeShellLauncher? shell = null,
        FakeConfirmationDialog? confirmation = null,
        TimeProvider? timeProvider = null) =>
        new(
            runner,
            () => Environment(runner, clipboard, shell, confirmation, timeProvider),
            NullLogger<TaskDevelopmentsViewModel>.Instance);

    /// <summary>O painel de um ambiente só.</summary>
    public static TaskDevelopmentViewModel Environment(
        FakeUseCaseRunner runner,
        FakeClipboardWriter? clipboard = null,
        FakeShellLauncher? shell = null,
        FakeConfirmationDialog? confirmation = null,
        TimeProvider? timeProvider = null) =>
        new(
            runner,
            clipboard ?? new FakeClipboardWriter(),
            shell ?? new FakeShellLauncher(),
            confirmation ?? new FakeConfirmationDialog(),
            timeProvider ?? TimeProvider.System,
            new AgentSessionViewModel(
                runner,
                clipboard ?? new FakeClipboardWriter(),
                shell ?? new FakeShellLauncher(),
                NullLogger<AgentSessionViewModel>.Instance),
            NullLogger<TaskDevelopmentViewModel>.Instance);

    /// <summary>Um ambiente gravado, como a consulta devolve.</summary>
    public static TaskDevelopmentView View(
        Guid taskId,
        TaskDevelopmentStatus status,
        string repository = @"C:\Projects\ecossistema-core",
        string branch = "feature/x",
        string? worktree = null,
        string? failure = null,
        IReadOnlyList<string>? commands = null,
        Guid? id = null) =>
        new(
            id ?? Guid.CreateVersion7(),
            taskId,
            repository,
            "origin/develop",
            branch,
            worktree ?? repository + "-feature-x",
            status,
            DateTimeOffset.UnixEpoch,
            failure,
            commands);

    /// <summary>A lista que <see cref="GetTaskDevelopmentsHandler"/> devolve.</summary>
    public static IReadOnlyList<TaskDevelopmentView> List(params TaskDevelopmentView?[] views) =>
        [.. views.OfType<TaskDevelopmentView>()];
}
