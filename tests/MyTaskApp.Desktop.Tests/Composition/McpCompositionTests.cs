using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Desktop.Tests.Composition;

/// <summary>
/// O composition root de verdade monta o servidor MCP (ADR-059): o gerente, o
/// gateway e a janela saem do mesmo contêiner do app, e o gerente é um só.
/// </summary>
public class McpCompositionTests
{
    [AvaloniaFact]
    public void TheAppContainer_BuildsTheMcpServer_AndItsWindow()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mytaskapp-composition-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Directory"] = directory })
            .Build();

        using var instance = SingleInstance.Acquire();
        using var services = AppServices.Build(configuration, instance, LaunchOptions.Manual);

        var manager = services.GetRequiredService<IMcpServerManager>();

        manager.Should().BeSameAs(services.GetRequiredService<McpServerManager>());
        manager.Status.State.Should().Be(McpServerState.Stopped, "nada sobe só por montar o contêiner");
        services.GetRequiredService<McpGateway>().Should().NotBeNull();
        services.GetRequiredService<IDataChangeNotifier>().Should().BeSameAs(services.GetRequiredService<IDataChangeNotifier>());
        services.GetRequiredService<McpServerViewModel>().ToolCount.Should().Be(McpCatalog.Tools.Count);
        services.GetRequiredService<McpServerWindow>().DataContext.Should().BeOfType<McpServerViewModel>();
    }
}
