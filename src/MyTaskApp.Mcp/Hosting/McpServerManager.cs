using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Mcp;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain;

namespace MyTaskApp.Mcp.Hosting;

/// <summary>
/// Sobe e derruba o servidor MCP dentro do processo do app (ADR-059). Kestrel só
/// no <c>127.0.0.1</c>, transporte Streamable HTTP do SDK oficial, sem sessão
/// (stateless): cada requisição é independente, e parar não deixa nada pendurado.
/// </summary>
/// <remarks>
/// <para>
/// O Kestrel tem o próprio contêiner, e ele não cria casos de uso: recebe do app
/// os singletons de que as ferramentas precisam — o <see cref="McpGateway"/>,
/// que tem o <see cref="IUseCaseRunner"/> — e cada ferramenta roda no escopo do
/// app, com o DbContext do app, como um clique na tela.
/// </para>
/// <para>
/// Duas instâncias não disputam o endpoint: o app já é de instância única por
/// sessão (ADR-019), e entre sessões a porta é aberta com uso exclusivo — quem
/// chega depois recebe "porta em uso", não um servidor compartilhado.
/// </para>
/// </remarks>
public sealed class McpServerManager : IMcpServerManager, IDisposable, IAsyncDisposable
{
    /// <summary>Corpo maior que isso não é uma chamada de ferramenta.</summary>
    internal const long MaxRequestBodyBytes = 1024 * 1024;

    internal const int MaxConnections = 64;

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    private readonly IUseCaseRunner _runner;
    private readonly McpGateway _gateway;
    private readonly McpAccessGuard _guard;
    private readonly McpServerRuntime _runtime;
    private readonly IUserClock _clock;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpServerManager> _logger;
    private readonly SemaphoreSlim _transition = new(1, 1);

    private WebApplication? _app;
    private McpServerStatus _status;
    private volatile bool _disposed;

    /// <summary>1 depois do primeiro descarte: o app e o contêiner descartam o mesmo gerente, e só o primeiro espera.</summary>
    private int _disposing;

    public McpServerManager(
        IUseCaseRunner runner,
        McpGateway gateway,
        McpAccessGuard guard,
        McpServerRuntime runtime,
        McpActivityLog activity,
        IUserClock clock,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        _runner = runner;
        _gateway = gateway;
        _guard = guard;
        _runtime = runtime;
        _clock = clock;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<McpServerManager>();
        Activity = activity;
        _status = McpServerStatus.Stopped(timeProvider.GetUtcNow());
    }

    public event Action<McpServerStatus>? StatusChanged;

    public McpServerStatus Status => Volatile.Read(ref _status);

    public McpActivityLog Activity { get; }

    public IReadOnlyList<McpToolDescriptor> Tools => McpCatalog.Tools;

    public async Task<McpServerStatus> StartAsync(CancellationToken cancellationToken = default)
    {
        await _transition.WaitAsync(cancellationToken);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_app is not null)
            {
                return Status;
            }

            var settings = await _runner.RunAsync<GetMcpServerSettingsHandler, McpServerSettings>(
                (handler, token) => handler.HandleAsync(new GetMcpServerSettings(), token),
                cancellationToken);

            if (!settings.Enabled)
            {
                throw new DomainException("Habilite o servidor MCP antes de iniciar.");
            }

            return await StartLockedAsync(settings, cancellationToken);
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task<McpServerStatus> StopAsync(CancellationToken cancellationToken = default)
    {
        await _transition.WaitAsync(cancellationToken);

        try
        {
            return await StopLockedAsync();
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task<McpServerStatus> ApplySettingsAsync(
        McpServerSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _transition.WaitAsync(cancellationToken);

        try
        {
            // Somente leitura vale na próxima chamada, sem reiniciar nada.
            _runtime.ReadOnly = settings.ReadOnly;

            if (_app is null)
            {
                return Status;
            }

            if (!settings.Enabled)
            {
                return await StopLockedAsync();
            }

            if (Status.Port != settings.Port)
            {
                await StopLockedAsync();
                return await StartLockedAsync(settings, cancellationToken);
            }

            return Status;
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task<McpServerStatus> StartIfConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _runner.RunAsync<GetMcpServerSettingsHandler, McpServerSettings>(
            (handler, token) => handler.HandleAsync(new GetMcpServerSettings(), token),
            cancellationToken);

        return settings is { Enabled: true, StartWithApp: true }
            ? await StartAsync(cancellationToken)
            : Status;
    }

    public Task<PortCheck> CheckPortAsync(int port, CancellationToken cancellationToken = default)
    {
        if (Status is { IsRunning: true } running && running.Port == port)
        {
            return Task.FromResult(new PortCheck(port, true, $"A porta {port} é a deste servidor, que está no ar."));
        }

        return Task.Run(() => PortProbe.Check(port), cancellationToken);
    }

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
        _runner.RunAsync<GetMcpAccessTokenHandler, string>(
            (handler, token) => handler.HandleAsync(new GetMcpAccessToken(), token),
            cancellationToken);

    public async Task<string> RegenerateAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        // Na mesma fila de subir e parar: um start no meio não põe o token velho de volta no guarda.
        await _transition.WaitAsync(cancellationToken);

        try
        {
            var token = await _runner.RunAsync<RegenerateMcpAccessTokenHandler, string>(
                (handler, cancel) => handler.HandleAsync(new RegenerateMcpAccessToken(), cancel),
                cancellationToken);

            _guard.SetToken(token);
            Record("Token de acesso trocado: clientes com o token antigo deixam de entrar.");

            return token;
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task<McpServerStatus> StartLockedAsync(McpServerSettings settings, CancellationToken cancellationToken)
    {
        Publish(new McpServerStatus(McpServerState.Starting, null, settings.Port, null, _timeProvider.GetUtcNow()));

        WebApplication? app = null;

        try
        {
            // O token antes da porta: não existe instante em que o servidor
            // escuta sem saber quem pode entrar.
            _guard.SetToken(await GetAccessTokenAsync(cancellationToken));
            _runtime.ReadOnly = settings.ReadOnly;

            app = Build(settings.Port);
            await app.StartAsync(cancellationToken);

            _app = app;

            var endpoint = McpServerSettings.EndpointFor(settings.Port);
            _logger.LogInformation("McpServerStarted {Endpoint} {ReadOnly}", endpoint, settings.ReadOnly);
            Record($"Servidor no ar em {endpoint}{(settings.ReadOnly ? " (somente leitura)" : string.Empty)}.");

            return Publish(new McpServerStatus(McpServerState.Running, endpoint, settings.Port, null, _timeProvider.GetUtcNow()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var message = Describe(settings.Port, exception);
            _logger.LogWarning(exception, "McpServerStartFailed {Port}", settings.Port);
            Record($"Não subiu: {message}");

            if (app is not null)
            {
                await DisposeQuietlyAsync(app);
            }

            return Publish(new McpServerStatus(McpServerState.Error, null, settings.Port, message, _timeProvider.GetUtcNow()));
        }
        catch (OperationCanceledException)
        {
            if (app is not null)
            {
                await DisposeQuietlyAsync(app);
            }

            Publish(McpServerStatus.Stopped(_timeProvider.GetUtcNow()));
            throw;
        }
    }

    private async Task<McpServerStatus> StopLockedAsync()
    {
        if (_app is not { } app)
        {
            // Parado, ou em erro: o pedido de parar limpa o erro.
            return Status.State is McpServerState.Stopped
                ? Status
                : Publish(McpServerStatus.Stopped(_timeProvider.GetUtcNow()));
        }

        Publish(Status with { State = McpServerState.Stopping, Since = _timeProvider.GetUtcNow() });

        _app = null;

        using (var timeout = new CancellationTokenSource(StopTimeout))
        {
            try
            {
                // Gracioso até o limite: quem está no meio de uma chamada termina;
                // depois disso, as conexões são cortadas.
                await app.StopAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("McpServerStopTimedOut");
            }
        }

        await DisposeQuietlyAsync(app);

        _logger.LogInformation("McpServerStopped");
        Record("Servidor parado.");

        return Publish(McpServerStatus.Stopped(_timeProvider.GetUtcNow()));
    }

    private WebApplication Build(int port)
    {
        // Vazio de propósito: sem appsettings, sem variáveis ASPNETCORE_*, sem
        // URLs vindas de fora. O único endereço é o que está aqui embaixo.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(McpServerManager).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,
        });

        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = MaxRequestBodyBytes;

            // Um cliente de IA abre poucas conexões; um teto evita que um processo
            // qualquer da máquina segure o servidor com milhares delas.
            options.Limits.MaxConcurrentConnections = MaxConnections;
            options.Listen(IPAddress.Loopback, port);
        });

        // Uso exclusivo da porta (Windows): sem isto, outro processo com
        // SO_REUSEADDR poderia escutar no mesmo endereço e receber os pedidos.
        builder.WebHost.UseSockets(sockets => sockets.CreateBoundListenSocket = endpoint =>
        {
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            if (OperatingSystem.IsWindows())
            {
                socket.ExclusiveAddressUse = true;
            }

            socket.Bind(endpoint);
            return socket;
        });

        // O log do app (Serilog), e não um segundo: registrado antes, o
        // AddLogging não o substitui. Instância de fora: o contêiner não a descarta.
        builder.Services.AddSingleton(_loggerFactory);
        builder.Services.AddLogging();
        builder.Services.AddRoutingCore();

        builder.Services.AddSingleton(_gateway);
        builder.Services.AddSingleton(_guard);
        builder.Services.AddSingleton(_runtime);
        builder.Services.AddSingleton(Activity);
        builder.Services.AddSingleton(_clock);
        builder.Services.AddSingleton(_timeProvider);
        builder.Services.AddSingleton<IMcpServerManager>(this);

        McpCatalog.Register(
            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new Implementation { Name = "mytaskapp", Title = "MyTaskApp", Version = Version };
                    options.ServerInstructions = McpCatalog.Instructions;
                })
                .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless));

        var app = builder.Build();

        app.UseMiddleware<McpSecurityMiddleware>();
        app.MapMcp("/mcp");

        return app;
    }

    private static string Version =>
        typeof(McpServerManager).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";

    private static string Describe(int port, Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket)
            {
                return PortProbe.Describe(port, socket);
            }

            if (current is IOException { Message: var text } && text.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            {
                return $"A porta {port} já está em uso por outro programa — ou por outra cópia do MyTaskApp. Escolha outra porta, ou feche quem a usa.";
            }

            if (current is DomainException domain)
            {
                return domain.Message;
            }
        }

        return $"Não foi possível iniciar na porta {port} ({exception.GetType().Name}). O detalhe está no log do app.";
    }

    private McpServerStatus Publish(McpServerStatus status)
    {
        Volatile.Write(ref _status, status);

        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "McpServerStatusHandlerFailed");
        }

        return status;
    }

    private void Record(string detail) =>
        Activity.Add(new McpActivityEntry(_timeProvider.GetUtcNow(), "servidor", McpActivityOutcome.Server, TimeSpan.Zero, detail));

    private async Task DisposeQuietlyAsync(WebApplication app)
    {
        try
        {
            await app.DisposeAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "McpServerDisposeFailed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) == 1)
        {
            return;
        }

        await _transition.WaitAsync();

        try
        {
            _disposed = true;
            await StopLockedAsync();
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>
    /// O contêiner do app descarta de forma síncrona, e um singleton só
    /// assíncrono o faria lançar. Espera no máximo o limite de parada mais um
    /// pouco: fechar o app nunca fica preso no servidor.
    /// </summary>
    public void Dispose()
    {
        if (Volatile.Read(ref _disposing) == 1)
        {
            return;
        }

        try
        {
            if (!Task.Run(() => DisposeAsync().AsTask()).Wait(StopTimeout + TimeSpan.FromSeconds(2)))
            {
                _logger.LogWarning("McpServerDisposeTimedOut");
            }
        }
        catch (AggregateException exception)
        {
            _logger.LogWarning(exception, "McpServerDisposeFailed");
        }
    }
}
