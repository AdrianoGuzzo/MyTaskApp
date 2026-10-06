using MyTaskApp.Application.Agents;

namespace MyTaskApp.Application.QuickCommands;

/// <summary>
/// Abre um terminal visível do sistema rodando uma linha de comando numa pasta
/// (ADR-051): <c>dotnet run</c> no worktree, sem o usuário abrir terminal nem
/// navegar até lá.
/// </summary>
/// <remarks>
/// Recebe a linha do usuário, e não programa e argumentos: comando rápido é
/// shell de propósito, com <c>&amp;&amp;</c>, pipes e aspas. Qual shell e como a
/// linha chega a ele é assunto da implementação, por sistema.
/// </remarks>
public interface ITerminalCommandLauncher
{
    /// <summary>Nunca lança por falha ao abrir: ela volta em <see cref="TerminalLaunchResult.Error"/>.</summary>
    Task<TerminalLaunchResult> LaunchAsync(
        TerminalCommandRequest request,
        CancellationToken cancellationToken = default);
}

/// <param name="Command">A linha final, já com variáveis e parâmetros preenchidos.</param>
/// <param name="WorkingDirectory">Pasta absoluta, que existe.</param>
/// <param name="KeepOpen">
/// A janela fica aberta quando o comando termina, para o usuário ler o que ele
/// disse. Fechada, o exit code do processo é o do comando.
/// </param>
public sealed record TerminalCommandRequest(string Command, string WorkingDirectory, bool KeepOpen);
