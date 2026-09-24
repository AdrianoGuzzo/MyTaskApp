using System.Runtime.InteropServices;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.Agents;

/// <summary>
/// Um agente de IA de linha de comando que o app sabe abrir num terminal
/// (ADR-030). Hoje só existe o Claude Code; Codex, Gemini e outros entram como
/// novas implementações, sem mexer no fluxo.
/// </summary>
/// <remarks>
/// O provider só descreve: onde está o executável, qual a versão, como se
/// instala e <b>o que</b> executar. Quem abre o terminal é o
/// <see cref="ITerminalLauncher"/>, e quem acompanha o processo é o monitor —
/// um agente novo não precisa saber de nenhum dos dois.
/// </remarks>
public interface IAgentCliProvider
{
    /// <summary>Identificador estável, gravado na sessão (ex.: <c>claude-code</c>).</summary>
    string Id { get; }

    /// <summary>Nome para a tela (ex.: "Claude Code").</summary>
    string Name { get; }

    /// <summary>O comando que o usuário digitaria (ex.: <c>claude</c>).</summary>
    string Command { get; }

    /// <summary>
    /// Os parâmetros com que o agente abre enquanto o usuário não escolher
    /// outros (ex.: <c>--dangerously-skip-permissions</c>).
    /// </summary>
    string DefaultArguments { get; }

    /// <summary>
    /// Procura o executável e, se achar, a versão — sem abrir sessão interativa.
    /// Nunca lança: o que der errado vai em <see cref="CliDetectionResult.Error"/>.
    /// </summary>
    Task<CliDetectionResult> DetectAsync(CancellationToken cancellationToken = default);

    /// <summary>Como instalar neste sistema; <c>null</c> se não houver guia para ele.</summary>
    AgentCliInstallGuide? InstallGuideFor(OSPlatform platform);

    /// <summary>O que o terminal deve executar para abrir o agente em <paramref name="context"/>.</summary>
    TerminalLaunchOptions CreateLaunch(AgentCliStartContext context, CliDetectionResult detection);
}

/// <summary>O agente está instalado? Onde, e em qual versão?</summary>
public sealed record CliDetectionResult
{
    public bool IsInstalled { get; init; }

    /// <summary>Caminho absoluto do executável encontrado.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary><c>null</c> quando o executável existe mas não disse a versão.</summary>
    public string? Version { get; init; }

    public string? Error { get; init; }

    public static CliDetectionResult NotInstalled(string error) => new() { Error = error };
}

/// <summary>Para qual tarefa, em qual pasta e com quais parâmetros o agente vai abrir.</summary>
public sealed record AgentCliStartContext(Guid TaskId, string WorkingDirectory, IReadOnlyList<string> Arguments);

/// <summary>Instruções de instalação de um agente para um sistema.</summary>
public sealed record AgentCliInstallGuide(
    string Title,
    IReadOnlyList<AgentCliInstallStep> Steps,
    Uri DocumentationUrl);

/// <summary>Um passo: o texto e, se houver, o comando a copiar.</summary>
public sealed record AgentCliInstallStep(string Text, string? Command = null);

/// <summary>Os agentes registrados, por id.</summary>
public interface IAgentCliProviders
{
    /// <summary>O agente usado quando a tela não escolhe — hoje, o único.</summary>
    IAgentCliProvider Default { get; }

    /// <summary>O agente com este id; <c>null</c> se nenhum foi registrado com ele.</summary>
    IAgentCliProvider? Find(string providerId);

    /// <summary><see cref="Default"/> para <c>null</c>; senão o do id, ou <see cref="DomainException"/>.</summary>
    IAgentCliProvider Get(string? providerId);
}

/// <summary>Catálogo sobre o que foi registrado no contêiner, na ordem de registro.</summary>
public sealed class AgentCliProviders(IEnumerable<IAgentCliProvider> providers) : IAgentCliProviders
{
    private readonly IReadOnlyList<IAgentCliProvider> _providers = [.. providers];

    public IAgentCliProvider Default =>
        _providers.Count > 0
            ? _providers[0]
            : throw new InvalidOperationException("Nenhum agente de linha de comando foi registrado.");

    public IAgentCliProvider? Find(string providerId) =>
        _providers.FirstOrDefault(
            provider => string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));

    public IAgentCliProvider Get(string? providerId) =>
        providerId is null
            ? Default
            : Find(providerId) ?? throw new DomainException($"Agente desconhecido: {providerId}.");
}
