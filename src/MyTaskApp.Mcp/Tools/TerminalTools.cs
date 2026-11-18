using System.ComponentModel;
using ModelContextProtocol.Server;
using MyTaskApp.Application.Remote;
using MyTaskApp.Mcp.Hosting;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Mcp.Tools;

[McpServerToolType]
public sealed class TerminalTools(McpGateway gateway)
{
    [McpServerTool(Name = "terminal_sessions_list", Title = "Consultar terminais remotos", ReadOnly = true, Idempotent = true)]
    [Description("Lista os estados dos terminais remotos de Claude e Codex da conta, com a máquina e o último relato. Não executa comandos.")]
    public Task<IReadOnlyList<RemoteTerminalSessionView>> ListAsync(string? taskId = null, string? deviceId = null, CancellationToken cancellationToken = default) =>
        gateway.ReadAsync("terminal_sessions_list", async (runner, token) =>
        {
            var task = McpInput.OptionalId(taskId, "o id da tarefa");
            var device = McpInput.OptionalId(deviceId, "o id do aparelho");
            var list = await runner.RunAsync<IRemoteTerminalsApi, RemoteTerminalSessionList>((api, cancel) => api.ListTerminalsAsync(cancel), token);
            return (IReadOnlyList<RemoteTerminalSessionView>)list.Sessions.Where(view => (task is null || view.Session.TaskId == task) && (device is null || view.DeviceId == device)).ToList();
        }, cancellationToken);

    [McpServerTool(Name = "terminal_session_events", Title = "Histórico do terminal remoto", ReadOnly = true, Idempotent = true)]
    [Description("Consulta até cem transições do terminal, em ordem de sequência. Informe after com a última sequência para continuar.")]
    public Task<TerminalLifecyclePage> EventsAsync(string sessionId, long after = 0, CancellationToken cancellationToken = default) =>
        gateway.ReadAsync("terminal_session_events", (runner, token) => runner.RunAsync<ITerminalLifecycleApi, TerminalLifecyclePage>(
            (api, cancel) => api.EventsAsync(McpInput.Id(sessionId, "o id da sessão"), Math.Max(0, after), cancel), token), cancellationToken);
}
