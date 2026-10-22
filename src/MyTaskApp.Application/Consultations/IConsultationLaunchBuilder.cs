using MyTaskApp.Application.Agents;

namespace MyTaskApp.Application.Consultations;

/// <summary>
/// Um agente que sabe abrir uma Consulta Inteligente (ADR-081): só leitura,
/// nas pastas escolhidas, com as instruções geradas e — com banco — só as
/// ferramentas de consulta do MCP do app. Hoje, o Claude Code.
/// </summary>
/// <remarks>
/// É uma capacidade à parte do <see cref="IAgentCliProvider"/>: um agente
/// novo pode abrir no worktree sem saber restringir as próprias ferramentas,
/// e aí a consulta diz que ele não serve, em vez de abrir sem as travas.
/// </remarks>
public interface IConsultationLaunchBuilder
{
    /// <summary>
    /// O que o terminal executa. Grava as instruções (e a configuração do MCP,
    /// quando há banco) em arquivos da sessão, e nunca põe segredo neles: o
    /// segredo do MCP vai em <see cref="ConsultationLaunchRequest.Environment"/>.
    /// </summary>
    TerminalLaunchOptions CreateConsultationLaunch(ConsultationLaunchRequest request, CliDetectionResult detection);

}

/// <summary>Uma Consulta Inteligente a abrir (ou a retomar).</summary>
/// <param name="SessionId">A sessão do MyTaskApp: nomeia os arquivos da sessão.</param>
/// <param name="ConversationId">O id da conversa do agente; com <paramref name="Resume"/>, a conversa a retomar.</param>
/// <param name="Directories">As pastas, já conferidas: a primeira é onde o agente abre, as outras entram como adicionais.</param>
/// <param name="Instructions">O texto anexado às instruções do agente — sem segredo nenhum.</param>
/// <param name="Question">A primeira mensagem; <c>null</c> ao retomar.</param>
/// <param name="Tools">
/// O servidor MCP e as ferramentas da consulta; <c>null</c> = só o código, e
/// nenhum servidor MCP é carregado.
/// </param>
/// <param name="Environment">O que vai no ambiente do processo: o segredo do MCP e o dos avisos.</param>
/// <param name="Monitoring">O acompanhamento pelos hooks (ADR-037), como numa sessão de desenvolvimento.</param>
public sealed record ConsultationLaunchRequest(
    Guid SessionId,
    string ConversationId,
    bool Resume,
    string Title,
    IReadOnlyList<string> Directories,
    string Instructions,
    string? Question,
    ConsultationToolsAccess? Tools,
    IReadOnlyList<string> ExtraArguments,
    IReadOnlyDictionary<string, string> Environment,
    AgentMonitoring? Monitoring);

/// <summary>Onde o agente acha as ferramentas da consulta, e quais pode usar sem perguntar.</summary>
/// <param name="TokenVariable">A variável de ambiente que leva o segredo; o arquivo só a cita.</param>
public sealed record ConsultationToolsAccess(Uri Endpoint, string TokenVariable, IReadOnlyList<string> Tools);

/// <summary>
/// O servidor MCP do app, visto pela consulta (ADR-059, ADR-081): está no ar?
/// Em qual endereço? A implementação é do projeto do MCP.
/// </summary>
public interface IConsultationToolsEndpoint
{
    /// <summary><c>null</c> quando o servidor MCP está desligado.</summary>
    Uri? Address { get; }
}

/// <summary>Sem servidor MCP (testes, host sem MCP): a consulta só abre com o código.</summary>
public sealed class NoConsultationToolsEndpoint : IConsultationToolsEndpoint
{
    public Uri? Address => null;
}

/// <summary>
/// As ferramentas do MCP que só uma Consulta Inteligente enxerga (ADR-081), e
/// a variável de ambiente que leva o segredo dela. Os nomes evitam
/// <c>sql</c>, <c>run</c> e <c>exec</c>: nenhuma ferramenta do app roda
/// comando, e esta só lê.
/// </summary>
public static class ConsultationToolNames
{
    public const string DescribeDatabase = "consultation_database_describe";

    public const string ListTables = "consultation_table_list";

    public const string DescribeTable = "consultation_table_describe";

    public const string Query = "consultation_query";

    /// <summary>O segredo da sessão no ambiente do processo; o arquivo de configuração só cita o nome.</summary>
    public const string TokenVariable = "MYTASKAPP_CONSULTATION_TOKEN";

    public static IReadOnlyList<string> All { get; } = [DescribeDatabase, ListTables, DescribeTable, Query];
}
