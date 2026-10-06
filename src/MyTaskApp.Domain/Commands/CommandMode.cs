namespace MyTaskApp.Domain.Commands;

/// <summary>
/// Como um comando rápido roda (ADR-051). Os valores vão para o banco: não
/// reordene.
/// </summary>
public enum CommandMode
{
    /// <summary>
    /// Escondido, com o output capturado e o exit code na tela: <c>dotnet test</c>,
    /// <c>dotnet build</c>. É como os comandos pós-Worktree sempre rodaram.
    /// </summary>
    Execute = 0,

    /// <summary>
    /// Num terminal visível do sistema, que fica na mão do usuário:
    /// <c>dotnet run</c>, <c>npm run dev</c>, <c>docker compose up</c>.
    /// </summary>
    Terminal = 1,
}
