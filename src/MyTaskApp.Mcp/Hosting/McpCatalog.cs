using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using MyTaskApp.Mcp.Tools;

namespace MyTaskApp.Mcp.Hosting;

/// <summary>
/// O que o servidor oferece, num lugar só (ADR-059): os tipos registrados no
/// SDK e a lista que a tela mostra saem da mesma fonte.
/// </summary>
public static class McpCatalog
{
    internal static readonly Type[] ToolTypes =
    [
        typeof(ServerTools),
        typeof(TaskTools),
        typeof(TimeTools),
        typeof(DatabaseConnectionTools),
        typeof(AnonymizationProfileTools),
        typeof(ContextTools),
    ];

    public static IReadOnlyList<string> Conventions { get; } =
    [
        "Datas locais em AAAA-MM-DD e horas em HH:mm, no fuso do usuário (veja server_info). Instantes voltam em UTC (ISO 8601).",
        "Ids são GUIDs. Uma tarefa é identificada pelo id da tarefa; o occurrenceId é interno.",
        "Durações voltam em segundos, em horas decimais e escritas (\"1h 30min\").",
        "Erros de regra de negócio voltam como erro da ferramenta com a mensagem que o usuário leria na tela; nada foi gravado.",
        "Alterações de perfil de anonimização são pré-visualizadas por padrão (apply=false); com apply=true, o perfil é relido do banco.",
        "Senhas e tokens nunca entram nem saem pelo MCP. Nenhuma ferramenta executa SQL livre nem comandos do sistema.",
        "Toda alteração aparece na tela do MyTaskApp na hora e fica na auditoria com a origem \"(MCP)\".",
    ];

    public static string Instructions { get; } =
        "Servidor MCP do MyTaskApp, o gerenciador de tarefas e de horas do usuário. Use as ferramentas para consultar e alterar " +
        "tarefas, cronômetro e lançamentos de horas, etiquetas e diretórios, conexões de banco e perfis de anonimização. " +
        "Consulte antes de alterar (task_get, anonymization_profile_get_details) e não presuma dados que não vieram das ferramentas. " +
        string.Join(" ", Conventions);

    public static IReadOnlyList<McpToolDescriptor> Tools { get; } = Describe();

    internal static IMcpServerBuilder Register(IMcpServerBuilder builder)
    {
        foreach (var type in ToolTypes)
        {
            builder.Services.AddTransient(type);
        }

        // IEnumerable<Type> explícito: um Type[] casaria com WithTools<T>(target),
        // que procura ferramentas nos métodos do próprio array — e não acha nenhuma.
        return builder
            .WithTools((IEnumerable<Type>)ToolTypes, McpJson.Options)
            .WithResources([typeof(McpResources)])
            .WithPrompts([typeof(McpPrompts)], McpJson.Options);
    }

    private static List<McpToolDescriptor> Describe() =>
        ToolTypes
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(method => (Method: method, Tool: method.GetCustomAttribute<McpServerToolAttribute>()))
                .Where(entry => entry.Tool is not null)
                .Select(entry => new McpToolDescriptor(
                    entry.Tool!.Name ?? entry.Method.Name,
                    entry.Tool.Title ?? entry.Tool.Name ?? entry.Method.Name,
                    entry.Method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
                    entry.Tool.ReadOnly,
                    entry.Tool.Destructive,
                    Area(type))))
            .OrderBy(tool => tool.Area, StringComparer.Ordinal)
            .ThenBy(tool => tool.Name, StringComparer.Ordinal)
            .ToList();

    private static string Area(Type type) => type.Name switch
    {
        nameof(TaskTools) => "Tarefas",
        nameof(TimeTools) => "Horas",
        nameof(DatabaseConnectionTools) => "Conexões de banco",
        nameof(AnonymizationProfileTools) => "Anonimização",
        nameof(ContextTools) => "Contexto",
        _ => "Servidor",
    };
}

public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// O servidor MCP no contêiner do app (ADR-059): singletons, porque o
    /// servidor é um só e vive com o app. As ferramentas não são registradas
    /// aqui — vivem no contêiner do Kestrel e chegam ao app pelo gateway.
    /// </summary>
    public static IServiceCollection AddMcpServerHost(this IServiceCollection services)
    {
        services.AddSingleton<McpAccessGuard>();
        services.AddSingleton<McpServerRuntime>();
        services.AddSingleton<McpActivityLog>();
        services.AddSingleton<McpGateway>();
        services.AddSingleton<McpServerManager>();
        services.AddSingleton<IMcpServerManager>(provider => provider.GetRequiredService<McpServerManager>());

        return services;
    }
}
