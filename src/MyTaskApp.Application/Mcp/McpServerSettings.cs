using MyTaskApp.Domain;

namespace MyTaskApp.Application.Mcp;

/// <summary>
/// Como o servidor MCP local se comporta (ADR-059). Dado do usuário, como os
/// lembretes (ADR-014): mora no banco, em linha única, e não no
/// <c>appsettings.json</c>, que não é gravável.
/// </summary>
/// <remarks>
/// O endereço não é configurável de propósito: o servidor escuta só em
/// <c>127.0.0.1</c>. Uma opção de "escutar na rede" seria exatamente a
/// configuração acidental que o deixaria acessível de fora.
/// </remarks>
public sealed record McpServerSettings
{
    public const int DefaultPort = 5180;

    /// <summary>Abaixo disso são portas de sistema; ninguém quer o MCP disputando com elas.</summary>
    public const int MinPort = 1024;

    public const int MaxPort = 65535;

    /// <summary>
    /// Instalação nova: desligado. Atualizar o app nunca abre uma porta que o
    /// usuário não pediu.
    /// </summary>
    public static McpServerSettings Factory { get; } = new(
        enabled: false,
        port: DefaultPort,
        startWithApp: false,
        readOnly: false);

    public McpServerSettings(bool enabled, int port, bool startWithApp, bool readOnly)
    {
        if (port is < MinPort or > MaxPort)
        {
            throw new DomainException($"A porta do servidor MCP fica entre {MinPort} e {MaxPort}.");
        }

        Enabled = enabled;
        Port = port;
        StartWithApp = startWithApp;
        ReadOnly = readOnly;
    }

    /// <summary>Desabilitado, o servidor não sobe — nem pelo botão, nem com o app.</summary>
    public bool Enabled { get; }

    public int Port { get; }

    /// <summary>Sobe sozinho quando o app abre, se também estiver habilitado.</summary>
    public bool StartWithApp { get; }

    /// <summary>Só consultas: toda ferramenta que grava é recusada.</summary>
    public bool ReadOnly { get; }

    /// <summary>O endpoint efetivo para esta porta: sempre o loopback.</summary>
    public Uri Endpoint => EndpointFor(Port);

    public static Uri EndpointFor(int port) => new($"http://127.0.0.1:{port}/mcp");
}
