using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Tests.ViewModels;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Tests.Composition;

/// <summary>
/// "Qual versão você tem?" precisa responder com o commit (ADR-044). O número
/// sozinho não basta: duas builds locais do mesmo 1.1.0 podem ser códigos
/// diferentes.
/// </summary>
public class AppVersionTests
{
    private const string Sha = "a82f91c4d5e6f708192a3b4c5d6e7f8091a2b3c4";

    [Fact]
    public void TheSdkFormat_SplitsIntoVersionAndCommit()
    {
        var version = AppVersion.Parse($"1.5.0+{Sha}", "2026-10-02");

        version.Version.Should().Be("1.5.0");
        version.FullCommit.Should().Be(Sha);
        version.Commit.Should().Be("a82f91c");
        version.BuildDate.Should().Be("2026-10-02");
    }

    [Fact]
    public void TheMenuLine_FitsVersionCommitAndDate()
    {
        AppVersion.Parse($"1.5.0+{Sha}", "2026-10-02").Display
            .Should().Be("1.5.0 · a82f91c · 2026-10-02");
    }

    /// <summary>Build fora de um repositório Git: o SDK não tem SHA para colar.</summary>
    [Fact]
    public void WithoutACommit_OnlyTheVersionIsShown()
    {
        var version = AppVersion.Parse("1.5.0", null);

        version.Commit.Should().BeNull();
        version.Display.Should().Be("1.5.0");
        version.Details.Should().Contain("Commit: desconhecido");
    }

    [Fact]
    public void WithoutAnyVersion_SaysSoInsteadOfShowingNothing()
    {
        AppVersion.Parse(null, null).Version.Should().Be("desconhecida");
    }

    /// <summary>O que vai para um relato de problema leva o SHA inteiro.</summary>
    [Fact]
    public void TheCopiedDetails_CarryTheFullCommit()
    {
        var details = AppVersion.Parse($"1.5.0+{Sha}", "2026-10-02").Details;

        details.Should().Contain("Version: 1.5.0");
        details.Should().Contain($"Commit: {Sha}");
        details.Should().Contain("Build: 2026-10-02");
    }

    /// <summary>
    /// O assembly de verdade: se o csproj parar de gravar a data, ou o SDK de
    /// colar o SHA, a linha do menu perde metade da informação sem ninguém
    /// notar.
    /// </summary>
    [Fact]
    public void TheShippedAssembly_KnowsItsCommitAndBuildDate()
    {
        var current = AppVersion.Current;

        current.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+");
        current.FullCommit.Should().MatchRegex("^[0-9a-f]{40}$");
        current.BuildDate.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$");
    }

    [Fact]
    public void TheMenu_ShowsTheProductAndTheVersion()
    {
        var viewModel = ViewModel(new FakeClipboardWriter());

        viewModel.VersionLabel.Should().Be("MyTaskApp 1.5.0 · a82f91c · 2026-10-02");
    }

    [Fact]
    public async Task CopyingTheVersion_PutsTheDetailsOnTheClipboard()
    {
        var clipboard = new FakeClipboardWriter();
        var viewModel = ViewModel(clipboard);

        await viewModel.CopyVersionAsync();

        clipboard.LastWritten.Should().Contain($"Commit: {Sha}");
        viewModel.StatusMessage.Should().Be("Versão copiada.");
    }

    [Fact]
    public async Task CopyingTheVersion_WhenTheClipboardRefuses_SaysSo()
    {
        var clipboard = new FakeClipboardWriter { Refuses = true };
        var viewModel = ViewModel(clipboard);

        await viewModel.CopyVersionAsync();

        viewModel.ErrorMessage.Should().Be("Não foi possível copiar a versão.");
        viewModel.StatusMessage.Should().BeNull();
    }

    private static TodayViewModel ViewModel(FakeClipboardWriter clipboard) =>
        new(
            new FakeUseCaseRunner { Result = new TodayBoard(new DateOnly(2026, 10, 2), [], [], [], [], []) },
            new FakeConfirmationDialog(),
            clipboard,
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance)
        {
            Version = AppVersion.Parse($"1.5.0+{Sha}", "2026-10-02"),
        };
}
