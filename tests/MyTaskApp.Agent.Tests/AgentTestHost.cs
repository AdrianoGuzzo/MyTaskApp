using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Secrets;

namespace MyTaskApp.Agent.Tests;

/// <summary>
/// O agente montado pela composição de verdade (<see cref="AgentComposition.AddAgent"/>)
/// sobre uma pasta temporária. O cofre e a identidade do dispositivo ficam em
/// memória: nada chega à pasta de dados do usuário.
/// </summary>
internal sealed class AgentTestHost : IAsyncDisposable
{
    private AgentTestHost(string folder, ServiceProvider services)
    {
        Folder = folder;
        Services = services;
    }

    public string Folder { get; }

    public ServiceProvider Services { get; }

    public static ServiceCollection Compose(string folder, Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Directory"] = folder,
                ["Application:TimeZoneId"] = "UTC",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IDeviceIdentity>(new TestDeviceIdentity());
        services.AddAgent(configuration);

        // Depois da Infrastructure: o último registro é o que o contêiner entrega.
        services.AddSingleton<ISecretStore, MemorySecretStore>();
        configure?.Invoke(services);

        return services;
    }

    public static AgentTestHost Create(Action<IServiceCollection>? configure = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"mytaskapp-agent-{Guid.NewGuid():N}");
        return new AgentTestHost(folder, Compose(folder, configure).BuildServiceProvider(validateScopes: true));
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nunca chegou a criar.
        }
        catch (IOException)
        {
            // Antivírus segurando o arquivo: fica na pasta temporária.
        }
    }
}

internal sealed class TestDeviceIdentity : IDeviceIdentity
{
    public Guid DeviceId { get; private set; } = Guid.CreateVersion7();

    public string DefaultNickname => "Agente de teste";

    public void Rotate() => DeviceId = Guid.CreateVersion7();
}

internal sealed class MemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new();

    public Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_secrets.TryGetValue(name, out var value) ? value : null);

    public Task WriteAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        _secrets[name] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        _secrets.TryRemove(name, out _);
        return Task.CompletedTask;
    }
}
