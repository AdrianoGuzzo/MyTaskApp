using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Processes;
using MyTaskApp.Infrastructure.Storage;
using Serilog;
using Serilog.Settings.Configuration;

namespace MyTaskApp.Agent;

internal static class Program
{
    private const string LogTemplate = "mytaskapp-agent-.log";

    private const int RetainedLogs = 7;

    /// <summary>
    /// Só os sinks do agente, sem varrer a pasta: instalado ao lado do desktop,
    /// ele veria o <c>Serilog.Sinks.Console.dll</c> de lá, que o deps.json
    /// daqui não deixa carregar — e o agente morreria antes do primeiro log.
    /// </summary>
    private static readonly ConfigurationReaderOptions SinkAssemblies =
        new(typeof(FileLoggerConfigurationExtensions).Assembly);

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--version"])
        {
            Console.WriteLine(Version);
            return 0;
        }

        // Antes do log: um segundo agente não escreve nada, só sai.
        using var instance = AgentInstance.Acquire();

        if (!instance.IsOwner)
        {
            return 0;
        }

        UserDataLocation.Current.CreateDirectories();
        Log.Logger = FileLogger(new LoggerConfiguration().MinimumLevel.Information());

        try
        {
            var configuration = AgentComposition.BuildConfiguration();

            // Troca só depois de o configurado existir: uma falha aqui ainda cai no log de partida.
            var configured = FileLogger(new LoggerConfiguration()
                .ReadFrom.Configuration(configuration, SinkAssemblies)
                .Enrich.FromLogContext());
            await Log.CloseAndFlushAsync();
            Log.Logger = configured;

            // Antes de qualquer filho: o comando pedido de longe, e o neto que ele abrir, morrem com o agente.
            KeepNoOrphans();

            await using var services = new ServiceCollection()
                .AddLogging(builder => builder.AddSerilog(Log.Logger))
                .AddAgent(configuration)
                .BuildServiceProvider(validateScopes: true);

            return await RunAsync(services);
        }
        catch (Exception exception)
        {
            // Também no stderr: sem console no Windows ninguém vê, mas no Linux o journal do systemd guarda.
            Log.Fatal(exception, "AgentUnhandledException");
            await Console.Error.WriteLineAsync($"AgentUnhandledException: {exception}");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>
    /// Sobe, espera o pedido de parar (SIGTERM do systemd, Ctrl+C num
    /// terminal) e para dentro da folga. No Windows, sem console, a saída é o
    /// instalador ou o fim da sessão encerrando o processo: o que estava no
    /// meio é retomado pelo diário na próxima vez (seção 4.5 da arquitetura).
    /// </summary>
    internal static async Task<int> RunAsync(ServiceProvider services, CancellationToken stop = default)
    {
        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        }

        var logger = services.GetRequiredService<ILogger<AgentHost>>();
        logger.LogInformation("AgentStarting {Version}", Version);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);

        var host = services.GetRequiredService<AgentHost>();
        host.Start();

        try
        {
            await Task.Delay(Timeout.Infinite, stopping.Token);
        }
        catch (OperationCanceledException)
        {
            // Pediram para parar.
        }

        try
        {
            await host.StopAsync().WaitAsync(AgentHost.ShutdownGrace);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("AgentShutdownTimedOut");
        }

        return 0;

        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            stopping.Cancel();
        }
    }

    /// <summary>
    /// O agente num Job Object que morre com ele (Windows). Sem o job, o
    /// agente ainda serve; só a queda dele deixaria o comando órfão, e o log diz.
    /// </summary>
    private static void KeepNoOrphans()
    {
        try
        {
            if (OperatingSystem.IsWindows() && KillOnCloseJob.KillChildrenWithThisProcess())
            {
                Log.Information("AgentProcessJobAttached");
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "AgentProcessJobFailed");
        }
    }

    private static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private static Serilog.ILogger FileLogger(LoggerConfiguration configuration) =>
        configuration
            .WriteTo.File(
                Path.Combine(UserDataLocation.Current.Logs, LogTemplate),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedLogs,

                // O desktop e o agente escrevem em arquivos diferentes; o compartilhado deixa um segundo leitor abrir.
                shared: true)
            .CreateLogger();
}
