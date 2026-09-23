using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A aba Desenvolvimento com dublês, para os testes da anotação que não se
/// interessam por ela (ADR-027).
/// </summary>
internal static class TestDevelopment
{
    public static TaskDevelopmentViewModel For(
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
            NullLogger<TaskDevelopmentViewModel>.Instance);
}
