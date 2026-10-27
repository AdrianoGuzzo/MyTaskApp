namespace MyTaskApp.Application.Agents;

/// <summary>
/// Um agente de IA sem terminal (ADR-089): cada prompt é um processo que lê o
/// texto do stdin, escreve eventos na saída e sai. É o modo da sessão pedida
/// de longe; o interativo (<see cref="IAgentCliProvider"/>) continua sendo o do
/// card, com o usuário olhando.
/// </summary>
/// <remarks>
/// O provider só descreve: a linha de comando e como ler uma linha da saída.
/// Quem roda o processo é o <see cref="IHeadlessAgentRunner"/>, e quem decide
/// o perfil, os tetos e a aprovação é o executor da operação.
/// </remarks>
public interface IHeadlessAgentProvider
{
    /// <summary>O mesmo id do interativo (ex.: <c>claude-code</c>), gravado na sessão.</summary>
    string Id { get; }

    string Name { get; }

    /// <inheritdoc cref="IAgentCliProvider.DetectAsync"/>
    Task<CliDetectionResult> DetectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Por que esta instalação não serve para o modo sem terminal (versão
    /// antiga ou desconhecida); <c>null</c> = serve. Os flags do modo foram
    /// conferidos numa versão mínima, e antes dela podem não existir.
    /// </summary>
    string? HeadlessUnavailableReason(CliDetectionResult detection);

    /// <summary>
    /// A linha de comando de um prompt. Lança <see cref="Domain.DomainException"/>
    /// quando o pedido escaparia das travas (modelo ou esforço desconhecido,
    /// perfil que pularia as permissões).
    /// </summary>
    HeadlessAgentLaunch CreateHeadlessLaunch(HeadlessAgentRequest request, CliDetectionResult detection);

    /// <summary>
    /// Os eventos de uma linha da saída. Linha que não é evento (aviso, lixo,
    /// tipo novo de uma versão mais nova) volta vazia: nunca lança.
    /// </summary>
    IReadOnlyList<HeadlessAgentEvent> ReadEvents(string line);

    /// <summary>A entrada de uma ferramenta em uma linha, para a pessoa decidir (o comando, o arquivo).</summary>
    string Summarize(string tool, System.Text.Json.JsonElement input);
}

/// <summary>
/// O que o agente pode usar sem perguntar, o que pode pedir e o que nunca usa.
/// Os perfis são locais (ADR-089): o controlador escolhe um pelo id, e quem
/// decide o que ele libera é a máquina que roda.
/// </summary>
/// <param name="Tools">As ferramentas que existem na sessão (<c>--tools</c>). O que não está aqui nem aparece para o modelo.</param>
/// <param name="AllowedTools">Os padrões que dispensam pergunta (<c>--allowedTools</c>, ex.: <c>Bash(dotnet test:*)</c>).</param>
/// <param name="DisallowedTools">Os padrões recusados sem pergunta (<c>--disallowedTools</c>).</param>
public sealed record AgentToolProfile(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> DisallowedTools);

/// <summary>Onde o agente pergunta antes de usar o que o perfil não libera: o MCP de aprovação, em loopback.</summary>
/// <param name="TokenVariable">A variável de ambiente com o segredo da sessão: o arquivo do MCP só cita o nome.</param>
public sealed record HeadlessApprovalAccess(Uri Endpoint, string ServerName, string ToolName, string TokenVariable, string Token);

/// <summary>Um prompt de uma conversa.</summary>
/// <param name="ConversationId">O id da conversa no agente: o primeiro prompt a cria, os seguintes continuam (<paramref name="Resume"/>).</param>
/// <param name="Approval">Nulo: o que perguntaria é negado (<c>--permission-prompts none</c>).</param>
/// <param name="DeniedPaths">Pastas que o agente nunca lê nem edita, além das de sempre (os dados do app).</param>
public sealed record HeadlessAgentRequest(
    string WorkingDirectory,
    Guid ConversationId,
    bool Resume,
    AgentToolProfile Profile,
    string? Model,
    string? Effort,
    int MaxTurns,
    decimal MaxBudgetUsd,
    HeadlessApprovalAccess? Approval,
    IReadOnlyList<string> DeniedPaths);

/// <summary>A linha de comando de um prompt: o texto vai pelo stdin, nunca como argumento.</summary>
/// <param name="Environment">O que entra no ambiente do processo, além do herdado (já sem o que <see cref="HeadlessEnvironment"/> tira).</param>
/// <param name="TemporaryFiles">Os arquivos gerados para este processo (MCP, permissões): quem roda apaga no fim.</param>
public sealed record HeadlessAgentLaunch(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> TemporaryFiles);

/// <summary>Um evento da saída do agente, já sem o que é só do protocolo dele.</summary>
public abstract record HeadlessAgentEvent;

/// <summary>A conversa abriu: o id que o agente usou, o modelo e as ferramentas que ele de fato tem.</summary>
public sealed record HeadlessSessionInitialized(string SessionId, string? Model, IReadOnlyList<string> Tools, string? Version) : HeadlessAgentEvent;

/// <summary>Texto do modelo para a pessoa.</summary>
public sealed record HeadlessText(string Text) : HeadlessAgentEvent;

/// <summary>O modelo pediu uma ferramenta; <paramref name="Summary"/> é a entrada resumida (o comando, o arquivo, o padrão).</summary>
public sealed record HeadlessToolUse(string ToolUseId, string Name, string Summary) : HeadlessAgentEvent;

/// <summary>A ferramenta respondeu, ou foi recusada (<paramref name="IsError"/>).</summary>
public sealed record HeadlessToolResult(string ToolUseId, bool IsError, string Summary) : HeadlessAgentEvent;

/// <summary>O fim do prompt.</summary>
/// <param name="IsError">O que vale: o <c>subtype</c> diz <c>success</c> até numa falha de login.</param>
/// <param name="DeniedTools">As ferramentas recusadas no caminho, na ordem.</param>
public sealed record HeadlessResult(
    bool IsError,
    string? Subtype,
    string? Text,
    int Turns,
    decimal? CostUsd,
    IReadOnlyList<string> DeniedTools,
    string? TerminalReason) : HeadlessAgentEvent;

/// <summary>Roda um prompt: escreve o texto no stdin, entrega cada linha da saída e espera o fim.</summary>
public interface IHeadlessAgentRunner
{
    /// <param name="started">O id do processo e quando ele começou, assim que ele existe: a sessão grava para a reconciliação.</param>
    /// <param name="line">Cada linha do stdout, na ordem, uma de cada vez.</param>
    /// <param name="cancellationToken">Cancelado: a árvore do processo é encerrada.</param>
    Task<HeadlessRunResult> RunAsync(
        HeadlessAgentLaunch launch,
        string prompt,
        Func<int, DateTimeOffset, Task> started,
        Func<string, Task> line,
        CancellationToken cancellationToken);
}

/// <param name="StandardError">O fim do stderr, com teto: só para o diagnóstico.</param>
public sealed record HeadlessRunResult(int ExitCode, string StandardError, bool Canceled);

/// <summary>
/// O ambiente do processo sem terminal (ADR-089, item 6): sem as senhas do
/// Postgres, sem os tokens do GitHub e sem as variáveis do app que não são da
/// sessão. O agente herda o ambiente do usuário, e o Claude não precisa disso.
/// </summary>
public static class HeadlessEnvironment
{
    private static readonly string[] Prefixes = ["PG", "MYTASKAPP_"];

    private static readonly string[] Names = ["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "CLAUDECODE"];

    /// <summary>A variável sai do ambiente herdado? As que a sessão põe (<paramref name="session"/>) ficam.</summary>
    public static bool IsRemoved(string name, IReadOnlyDictionary<string, string> session) =>
        !session.ContainsKey(name)
        && (Names.Contains(name, StringComparer.OrdinalIgnoreCase)
            || Prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
}
