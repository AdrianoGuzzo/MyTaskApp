using System.Text.Json;

namespace MyTaskApp.Application.Agents;

/// <summary>
/// O canal por onde o agente sem terminal pergunta antes de usar o que o
/// perfil não libera (ADR-089, item 5): o MCP de aprovação, em loopback, com
/// um segredo por sessão. Só o processo do agente em segundo plano o oferece;
/// sem ele, a sessão nega o que perguntaria.
/// </summary>
public interface IHeadlessApprovalGateway
{
    /// <summary>
    /// Abre o canal de uma sessão. Cada pergunta do agente chega em
    /// <paramref name="decide"/>; fechar o canal (<see cref="IAsyncDisposable"/>)
    /// mata o segredo, e o que chegar depois é negado.
    /// </summary>
    Task<IHeadlessApprovalChannel> OpenAsync(ToolPermissionDecider decide, CancellationToken cancellationToken);
}

/// <summary>Quem decide se a ferramenta roda: no executor, a pessoa do outro lado.</summary>
public delegate Task<ToolPermissionDecision> ToolPermissionDecider(ToolPermissionRequest request, CancellationToken cancellationToken);

/// <summary>Um canal aberto: o endereço e o segredo que vão na linha de comando do agente.</summary>
public interface IHeadlessApprovalChannel : IAsyncDisposable
{
    HeadlessApprovalAccess Access { get; }
}

/// <summary>O que o agente quer usar, como ele mandou (a entrada é dele, e não confiável).</summary>
public sealed record ToolPermissionRequest(string ToolName, JsonElement Input, string? ToolUseId);

/// <summary>A resposta ao agente. Negar leva o motivo, que o modelo lê.</summary>
public sealed record ToolPermissionDecision(bool IsAllowed, string? Message)
{
    public static ToolPermissionDecision Allow() => new(true, null);

    public static ToolPermissionDecision Deny(string message) => new(false, message);
}

/// <summary>
/// Os perfis do agente sem terminal (ADR-089, item 2): locais, fixos no
/// código. O controlador escolhe pelo id, e o que cada um libera é decidido
/// aqui. O que fica fora de <see cref="AgentToolProfile.AllowedTools"/> e
/// dentro de <see cref="AgentToolProfile.Tools"/> pergunta.
/// </summary>
/// <remarks>
/// O <c>--restricted</c> mantém as ferramentas que o <c>--tools</c> nomeia,
/// o Bash também (conferido no 2.1.310). Por isso só os perfis que editam
/// têm Bash, e nenhum o libera inteiro: só comandos de leitura do Git e, no
/// de testes, os de build e teste. <c>git push</c> e apagar em massa são
/// recusados sem pergunta.
/// </remarks>
public static class AgentToolProfiles
{
    public const string ReadId = "read";

    public const string EditId = "edit";

    public const string TestId = "edit-test";

    private static readonly string[] Reading = ["Read", "Grep", "Glob"];

    private static readonly string[] Editing = ["Edit", "Write", "NotebookEdit"];

    private static readonly string[] GitReading =
    [
        "Bash(git status:*)",
        "Bash(git diff:*)",
        "Bash(git log:*)",
        "Bash(git show:*)",
        "Bash(git branch:*)",
    ];

    private static readonly string[] Testing =
    [
        "Bash(dotnet build:*)",
        "Bash(dotnet test:*)",
        "Bash(npm test:*)",
        "Bash(npm run test:*)",
        "Bash(npm run build:*)",
        "Bash(pytest:*)",
    ];

    private static readonly string[] Never =
    [
        "Bash(git push:*)",
        "Bash(git remote:*)",
        "Bash(git config:*)",
        "Bash(rm -rf:*)",
        "Bash(curl:*)",
        "Bash(wget:*)",
        "WebFetch",
        "WebSearch",
    ];

    public static readonly AgentToolProfile Read = new(
        ReadId,
        "Ler",
        "Lê e procura nos arquivos do worktree. Não edita nem roda comandos.",
        Reading,
        Reading,
        []);

    public static readonly AgentToolProfile Edit = new(
        EditId,
        "Editar no worktree",
        "Lê e edita os arquivos do worktree. Comandos, fora os de leitura do Git, pedem aprovação.",
        [.. Reading, .. Editing, "Bash"],
        [.. Reading, .. Editing, .. GitReading],
        Never);

    public static readonly AgentToolProfile Test = new(
        TestId,
        "Editar e rodar testes",
        "Como \"Editar no worktree\", e roda build e testes sem perguntar. Outros comandos pedem aprovação.",
        [.. Reading, .. Editing, "Bash"],
        [.. Reading, .. Editing, .. GitReading, .. Testing],
        Never);

    public static IReadOnlyList<AgentToolProfile> All { get; } = [Read, Edit, Test];

    public static AgentToolProfile? Find(string? id) =>
        All.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.Ordinal));
}
