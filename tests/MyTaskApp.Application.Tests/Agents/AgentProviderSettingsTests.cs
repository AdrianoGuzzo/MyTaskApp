using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using NSubstitute;

namespace MyTaskApp.Application.Tests.Agents;

public class AgentProviderSettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DefaultIsClaudeUntilTheInstallationChoosesAnotherProvider()
    {
        var claude = new FakeAgentCliProvider();
        var codex = Substitute.For<IAgentCliProvider>();
        codex.Id.Returns("codex");
        codex.Name.Returns("Codex");
        var providers = new AgentCliProviders([claude, codex]);
        var settings = new FakeAgentSettingsStore();
        var unitOfWork = new FakeTaskItemRepository();
        var handler = new AgentProviderSettingsHandler(providers, settings, unitOfWork);

        (await handler.GetAsync(Ct)).DefaultProviderId.Should().Be("claude-code");
        await handler.SaveAsync("codex", Ct);
        (await handler.GetAsync(Ct)).DefaultProviderId.Should().Be("codex");
        (await providers.ResolveAsync(settings, "claude-code", Ct)).Should().BeSameAs(claude);
        (await providers.ResolveAsync(settings, null, Ct)).Should().BeSameAs(codex);
    }

    [Fact]
    public async Task AnUnavailableSavedDefaultFallsBackButAnExplicitUnknownProviderIsRefused()
    {
        var providers = new AgentCliProviders([new FakeAgentCliProvider()]);
        var settings = new FakeAgentSettingsStore { DefaultProviderId = "removed-provider" };
        (await providers.ResolveAsync(settings, null, Ct)).Id.Should().Be("claude-code");
        var explicitChoice = () => providers.ResolveAsync(settings, "removed-provider", Ct);
        await explicitChoice.Should().ThrowAsync<DomainException>();
    }
}
