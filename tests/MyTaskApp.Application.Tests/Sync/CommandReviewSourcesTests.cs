using MyTaskApp.Application.Sync;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Application.Tests.Sync;

/// <summary>O que o portão de revisão confere numa execução (ADR-061): os globais chamados por @alias.</summary>
public sealed class CommandReviewSourcesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnlyTheGlobalsCalledByTheEntries_AreChecked()
    {
        var build = DevelopmentCommand.Create("@build", "dotnet build", null, Now);
        var deploy = DevelopmentCommand.Create("@deploy", "./deploy.sh", null, Now);

        var sources = CommandReviewSources.GlobalsUsedBy(["@BUILD --no-restore", "git status"], [build, deploy]).ToList();

        sources.Should().ContainSingle().Which.Should().Be(new CommandSource(SyncEntityTypes.Command, build.Id));
    }

    [Fact]
    public async Task WithoutSync_EverythingPasses()
    {
        var gate = new NoCommandReviewGate();

        var check = () => gate.EnsureReviewedAsync([new CommandSource(SyncEntityTypes.Command, Guid.CreateVersion7())], TestContext.Current.CancellationToken);

        await check.Should().NotThrowAsync();
    }
}
