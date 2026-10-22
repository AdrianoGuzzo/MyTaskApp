using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using MyTaskApp.Application.Consultations;

namespace MyTaskApp.Mcp.Hosting;

/// <summary>
/// O que cada lado enxerga (ADR-081). O Claude de uma Consulta Inteligente vê
/// e chama só as ferramentas de consulta — nada de tarefas, horas, conexões,
/// recursos ou prompts. O cliente do token do servidor vê tudo, menos elas.
/// </summary>
/// <remarks>
/// Filtros do SDK, pelo <see cref="McpCaller"/> que o
/// <see cref="McpSecurityMiddleware"/> pôs na requisição. O
/// <see cref="McpGateway"/> confere de novo: um filtro esquecido não abre nada.
/// </remarks>
internal static class McpConsultationFilters
{
    public static readonly HashSet<string> Tools = new(ConsultationToolNames.All, StringComparer.Ordinal);

    public static void Apply(IMcpRequestFilterBuilder filters)
    {
        filters
            .AddListToolsFilter(next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken);
                var consultation = McpCaller.ConsultationOf(context.User) is not null;

                result.Tools = [.. result.Tools.Where(tool => Tools.Contains(tool.Name) == consultation)];
                return result;
            })
            .AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                var access = McpCaller.ConsultationOf(context.User);
                var name = context.Params?.Name ?? string.Empty;

                if (access is not null && !Tools.Contains(name))
                {
                    return Refused("Numa Consulta Inteligente, só as ferramentas de consulta do banco estão disponíveis.");
                }

                if (access is null && Tools.Contains(name))
                {
                    return Refused(
                        "Esta ferramenta só existe numa sessão de Consulta Inteligente, aberta pela aba da tarefa no MyTaskApp.");
                }

                // A sessão chega à ferramenta por aqui, no fluxo da chamada.
                using (McpCaller.Enter(access))
                {
                    return await next(context, cancellationToken);
                }
            })
            .AddListResourcesFilter(next => async (context, cancellationToken) =>
                McpCaller.ConsultationOf(context.User) is null ? await next(context, cancellationToken) : new ListResourcesResult())
            .AddListResourceTemplatesFilter(next => async (context, cancellationToken) =>
                McpCaller.ConsultationOf(context.User) is null ? await next(context, cancellationToken) : new ListResourceTemplatesResult())
            .AddReadResourceFilter(next => async (context, cancellationToken) =>
                McpCaller.ConsultationOf(context.User) is null
                    ? await next(context, cancellationToken)
                    : throw new McpException("Numa Consulta Inteligente, os recursos do MyTaskApp não estão disponíveis."))
            .AddListPromptsFilter(next => async (context, cancellationToken) =>
                McpCaller.ConsultationOf(context.User) is null ? await next(context, cancellationToken) : new ListPromptsResult())
            .AddGetPromptFilter(next => async (context, cancellationToken) =>
                McpCaller.ConsultationOf(context.User) is null
                    ? await next(context, cancellationToken)
                    : throw new McpException("Numa Consulta Inteligente, os prompts do MyTaskApp não estão disponíveis."));
    }

    private static CallToolResult Refused(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
