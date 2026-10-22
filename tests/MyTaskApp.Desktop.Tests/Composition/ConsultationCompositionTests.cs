using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Desktop.Tests.Composition;

/// <summary>
/// O composition root de verdade monta a Consulta Inteligente (ADR-081): a
/// aba na janela da tarefa, o agente que sabe abrir só leitura e o endereço do
/// MCP de verdade — e não o "sem MCP" da Application.
/// </summary>
public class ConsultationCompositionTests
{
    [AvaloniaFact]
    public void TheAppContainer_BuildsTheConsultationTab_WithTheRealPieces()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mytaskapp-composition-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Directory"] = directory })
            .Build();

        using var instance = SingleInstance.Acquire();
        using var services = AppServices.Build(configuration, instance, LaunchOptions.Manual);

        services.GetRequiredService<TaskNotesViewModel>().Consultation.Should().NotBeNull();
        services.GetRequiredService<IConsultationToolsEndpoint>().Should().BeOfType<McpConsultationEndpoint>()
            .Which.Address.Should().BeNull("o servidor MCP não sobe só por montar o contêiner");
        services.GetRequiredService<IAgentCliProviders>().Default.Should().BeAssignableTo<IConsultationLaunchBuilder>();

        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<StartTaskConsultationHandler>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<PrepareConsultationReadAccessHandler>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<CopyConsultationDatabaseHandler>().Should().NotBeNull();
    }
}
