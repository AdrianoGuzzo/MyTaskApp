namespace MyTaskApp.Desktop.Composition;

/// <summary>
/// Como o processo foi lançado. Hoje há uma pergunta só: foi o login do Windows
/// que abriu o app, ou foi uma pessoa? (ADR-023)
/// </summary>
/// <remarks>
/// <see cref="StartupFlag"/> é contrato com o instalador, do mesmo jeito que
/// <see cref="SingleInstance.MutexName"/>: é o argumento que o <c>.iss</c> grava
/// na chave <c>Run</c>. Há teste de packaging cobrando os dois lados — mudar um
/// sem o outro quebra a build, e não o login de alguém.
/// </remarks>
internal sealed record LaunchOptions(bool StartedByWindows)
{
    public const string StartupFlag = "--startup";

    /// <summary>O que vale quando ninguém passou argumento nenhum.</summary>
    public static LaunchOptions Manual { get; } = new(StartedByWindows: false);

    /// <summary>
    /// Um segundo lançamento normal revela a janela do primeiro (ADR-019). Um
    /// disparado pelo login, não: o usuário não clicou em nada, e fazer o painel
    /// pular na frente dele no boot seria o oposto do que a opção promete.
    /// </summary>
    public bool ShouldSignalExistingInstance => !StartedByWindows;

    public static LaunchOptions Parse(string[] args) =>
        new(args.Any(argument =>
            string.Equals(argument, StartupFlag, StringComparison.OrdinalIgnoreCase)));
}
