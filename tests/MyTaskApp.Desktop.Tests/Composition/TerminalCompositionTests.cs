using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Terminals;
using MyTaskApp.Infrastructure.Terminals;

namespace MyTaskApp.Desktop.Tests.Composition;

/// <summary>
/// O composition root de verdade liga o desktop ao terminal do agente
/// (ADR-098): o canal local até o agente, a vista dentro do app, e o caminho
/// único das telas para abrir o Claude.
/// </summary>
public class TerminalCompositionTests
{
    [AvaloniaFact]
    public void TheDesktop_TalksToTheAgentsTerminal_AndShowsItInsideTheApp()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mytaskapp-composition-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Directory"] = directory })
            .Build();

        using var instance = SingleInstance.Acquire();
        using var services = AppServices.Build(configuration, instance, LaunchOptions.Manual);

        services.GetRequiredService<IAgentTerminalClient>().Should().BeOfType<LocalAgentTerminalClient>();
        services.GetRequiredService<IEmbeddedTerminalPresenter>().Should().BeSameAs(services.GetRequiredService<EmbeddedTerminalWindows>());
        services.GetRequiredService<IAgentTerminalWindows>().Should().BeSameAs(services.GetRequiredService<AgentTerminalWindows>());

        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<OpenAgentSessionHandler>().Should().NotBeNull();
    }
}
