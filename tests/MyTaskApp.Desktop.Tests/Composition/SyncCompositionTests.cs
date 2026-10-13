using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Sync;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;

namespace MyTaskApp.Desktop.Tests.Composition;

/// <summary>
/// O composition root de verdade monta a janela da sincronização (ADR-061), e
/// a conta que ela usa é a mesma do resto do app.
/// </summary>
public class SyncCompositionTests
{
    [AvaloniaFact]
    public void TheAppContainer_BuildsTheSyncWindow_OnTheSameAccount()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mytaskapp-composition-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Directory"] = directory })
            .Build();

        using var instance = SingleInstance.Acquire();
        using var services = AppServices.Build(configuration, instance, LaunchOptions.Manual);

        services.GetRequiredService<ISyncAccount>().Should().BeSameAs(services.GetRequiredService<SyncAccountService>());
        services.GetRequiredService<SyncWindow>().DataContext.Should().BeSameAs(services.GetRequiredService<SyncViewModel>());
    }
}
