using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using MyTaskApp.Application.Planning;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Mcp.Tools;

/// <summary>O próprio servidor: versão, fuso, modo e o que existe.</summary>
[McpServerToolType]
public sealed class ServerTools(McpServerRuntime runtime, IUserClock clock, TimeProvider timeProvider)
{
    [McpServerTool(Name = "server_info", Title = "Sobre o servidor", ReadOnly = true, Idempotent = true)]
    [Description("Versão do MyTaskApp, fuso e data de hoje do usuário, se o servidor está em somente leitura e as convenções das ferramentas.")]
    public ServerInfo GetInfo() => new(
        "MyTaskApp",
        typeof(ServerTools).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        clock.TimeZone.Id,
        Text.Date(clock.Today),
        Text.Time(clock.CurrentTime),
        timeProvider.GetUtcNow(),
        runtime.ReadOnly,
        McpCatalog.Tools.Count,
        McpCatalog.Conventions);
}

public sealed record ServerInfo(
    string Application,
    string Version,
    string TimeZone,
    string Today,
    string Now,
    DateTimeOffset NowUtc,
    bool ReadOnly,
    int Tools,
    IReadOnlyList<string> Conventions);

/// <summary>
/// Contextos só de leitura (ADR-059): o mesmo que as ferramentas de consulta
/// devolvem, num endereço fixo, para o cliente anexar à conversa.
/// </summary>
[McpServerResourceType]
public sealed class McpResources(TaskTools tasks, TimeTools time, DatabaseConnectionTools connections, AnonymizationProfileTools profiles, ContextTools context)
{
    [McpServerResource(UriTemplate = "mytaskapp://today", Name = "today", Title = "Quadro de hoje", MimeType = "application/json")]
    [Description("As tarefas de hoje por seção e o cronômetro ativo.")]
    public async Task<string> TodayAsync(CancellationToken cancellationToken) =>
        Json(await tasks.GetTodayAsync(cancellationToken));

    [McpServerResource(UriTemplate = "mytaskapp://timer", Name = "timer", Title = "Cronômetro", MimeType = "application/json")]
    [Description("O cronômetro ativo, o total de hoje e o último período encerrado.")]
    public async Task<string> TimerAsync(CancellationToken cancellationToken) =>
        Json(await time.GetStatusAsync(cancellationToken));

    [McpServerResource(UriTemplate = "mytaskapp://time/week", Name = "time-week", Title = "Horas da semana", MimeType = "application/json")]
    [Description("As horas dos últimos 7 dias, por dia.")]
    public async Task<string> WeekAsync(IUserClock clock, CancellationToken cancellationToken) =>
        Json(await time.GetSummaryAsync(
            Text.Date(clock.Today.AddDays(-6)), Text.Date(clock.Today), cancellationToken: cancellationToken));

    [McpServerResource(UriTemplate = "mytaskapp://statistics", Name = "statistics", Title = "Estatísticas", MimeType = "application/json")]
    [Description("Contagens de tarefas e horas recentes.")]
    public async Task<string> StatisticsAsync(CancellationToken cancellationToken) =>
        Json(await tasks.GetStatisticsAsync(cancellationToken));

    [McpServerResource(UriTemplate = "mytaskapp://tags", Name = "tags", Title = "Etiquetas e diretórios", MimeType = "application/json")]
    [Description("As etiquetas e os diretórios (aliases) de cada uma.")]
    public async Task<string> TagsAsync(CancellationToken cancellationToken) =>
        Json(new
        {
            Tags = await context.ListTagsAsync(cancellationToken),
            Directories = await context.ListDirectoriesAsync(cancellationToken: cancellationToken),
        });

    [McpServerResource(UriTemplate = "mytaskapp://databases/connections", Name = "database-connections", Title = "Conexões de banco", MimeType = "application/json")]
    [Description("As conexões de banco, sem senha nem segredo.")]
    public async Task<string> ConnectionsAsync(CancellationToken cancellationToken) =>
        Json(await connections.ListAsync(cancellationToken));

    [McpServerResource(UriTemplate = "mytaskapp://anonymization-profiles", Name = "anonymization-profiles", Title = "Perfis de anonimização", MimeType = "application/json")]
    [Description("Os perfis de anonimização, em resumo.")]
    public async Task<string> ProfilesAsync(CancellationToken cancellationToken) =>
        Json(await profiles.ListAsync(cancellationToken));

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, McpJson.Options);
}

/// <summary>
/// Roteiros reutilizáveis (ADR-059). Cada um diz quais ferramentas consultar e
/// proíbe presumir: o que não veio de uma ferramenta não entra na resposta.
/// </summary>
[McpServerPromptType]
public sealed class McpPrompts
{
    private const string Rule =
        "Use só o que as ferramentas do MyTaskApp devolverem; não invente tarefas, horas nem regras, e diga quando um dado não existe. " +
        "Não altere nada sem eu pedir.";

    [McpServerPrompt(Name = "review_today", Title = "Revisar o dia")]
    [Description("Revisa as tarefas de hoje e sugere a ordem.")]
    public static string ReviewToday() =>
        "Revise meu dia no MyTaskApp. Chame task_get_today e time_tracking_get_status. Liste as atrasadas primeiro, depois as de agora, " +
        "as de hoje e os prazos, com o motivo de cada uma estar ali, e sugira uma ordem de trabalho considerando prioridade, prazo e " +
        "o que já tem tempo registrado. " + Rule;

    [McpServerPrompt(Name = "weekly_summary", Title = "Resumo semanal")]
    [Description("Prepara o resumo das atividades da semana.")]
    public static string WeeklySummary() =>
        "Prepare o resumo da minha semana. Chame task_get_weekly_history, time_entry_get_summary dos últimos 7 dias agrupado por Task e " +
        "depois por External, e task_get_statistics. Mostre o que foi concluído, onde foi o tempo e o total, citando os números exatos " +
        "que vieram das ferramentas. " + Rule;

    [McpServerPrompt(Name = "audit_time_entries", Title = "Conferir horas")]
    [Description("Procura inconsistências nos apontamentos de um período.")]
    public static string AuditTimeEntries(
        [Description("Primeiro dia (AAAA-MM-DD).")] string from,
        [Description("Último dia (AAAA-MM-DD).")] string to) =>
        $"Confira meus apontamentos de {from} a {to}. Chame time_entry_list com esse intervalo (pagine até o fim) e time_entry_get_summary " +
        "agrupado por Day. Aponte: períodos longos (isLong), que atravessam a meia-noite, cronômetro ainda correndo, dias sem nada, " +
        "lançamentos manuais e tarefas na lixeira ou arquivadas com horas. Para cada achado, cite o id do período. Proponha correções, " +
        "mas só use time_entry_update ou time_entry_delete se eu confirmar. " + Rule;

    [McpServerPrompt(Name = "plan_next_tasks", Title = "Planejar próximas tarefas")]
    [Description("Planeja as próximas tarefas a partir do que está pendente.")]
    public static string PlanNextTasks() =>
        "Me ajude a planejar as próximas tarefas. Chame task_get_overdue, deadline_list_upcoming e task_search com statuses=[Pending] " +
        "e sort=Priority. Considere estimativa e tempo registrado de cada uma. Proponha um plano para os próximos dias; se sugerir " +
        "mudar datas ou prazos, mostre a lista de alterações e só aplique com task_update depois que eu confirmar. " + Rule;

    [McpServerPrompt(Name = "task_dev_context", Title = "Contexto de desenvolvimento")]
    [Description("Junta o contexto de desenvolvimento de uma tarefa.")]
    public static string TaskDevContext([Description("O id da tarefa.")] string taskId) =>
        $"Reúna o contexto de desenvolvimento da tarefa {taskId}. Chame task_get, git_context_get, task_get_external_context, " +
        "directory_alias_list com taskId e command_binding_list. Resuma: o que a tarefa pede, a issue, os worktrees e o estado do Git, " +
        "os diretórios e os comandos disponíveis (eles não são executados pelo MCP). " + Rule;

    [McpServerPrompt(Name = "check_overdue", Title = "Verificar atrasadas")]
    [Description("Verifica as tarefas atrasadas e sugere o que fazer com cada uma.")]
    public static string CheckOverdue() =>
        "Verifique minhas tarefas atrasadas. Chame task_get_overdue e, para as que tiverem prazo, task_get. Para cada uma, diga há quanto " +
        "tempo está atrasada e sugira: fazer agora, reagendar, mudar o prazo ou cancelar. Não aplique nada sem confirmação. " + Rule;

    [McpServerPrompt(Name = "review_anonymization_profile", Title = "Revisar perfil de anonimização")]
    [Description("Revisa um perfil de anonimização antes de usá-lo numa cópia.")]
    public static string ReviewAnonymizationProfile([Description("O id do perfil.")] string profileId) =>
        $"Revise o perfil de anonimização {profileId}. Chame anonymization_profile_get_details, anonymization_profile_analyze e " +
        "anonymization_profile_get_schema. Separe o que é problema comprovado, risco e o que falta saber. Para cada correção, proponha " +
        "a alteração com anonymization_profile_rule_update ou _rule_create com apply=false, mostre o diff, e só repita com apply=true " +
        "e o expectedUpdatedAt lido se eu confirmar. Depois de gravar, consulte o perfil de novo e confirme o que ficou. Não diga que o " +
        "perfil garante conformidade com a LGPD. " + Rule;
}
