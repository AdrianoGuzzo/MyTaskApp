namespace MyTaskApp.Domain.Commands;

/// <summary>Em que pé está uma execução de comando rápido. Os valores vão para o banco: não reordene.</summary>
public enum CommandExecutionStatus
{
    /// <summary>Gravada, ainda sem processo: o registro vem antes do shell, para uma queda não deixar processo sem rastro.</summary>
    Queued = 1,

    Running = 2,

    /// <summary>Terminou com exit code 0, ou o terminal foi fechado.</summary>
    Completed = 3,

    /// <summary>Exit code diferente de 0, não iniciou, estourou o tempo ou foi interrompida pela queda do app.</summary>
    Failed = 4,

    /// <summary>O usuário cancelou.</summary>
    Stopped = 5,
}

/// <summary>
/// Uma execução de comando rápido (ADR-051): o que rodou, onde, quando, e como
/// terminou. É o histórico, e é também o que liga o botão ao processo que ele
/// abriu — PID e início, como a sessão do agente (ADR-030).
/// </summary>
/// <remarks>
/// Agregado próprio, e não filho da tarefa: quem muda o estado é um processo de
/// fora. Guarda <b>cópias</b> do nome, da linha e da pasta: editar o comando
/// global depois não reescreve o que já rodou.
/// </remarks>
public sealed class CommandExecution
{
    public const int MaxNameLength = 120;

    public const int MaxCommandLineLength = 8000;

    public const int MaxPathLength = 1024;

    /// <summary>O fim do output, que é onde está o erro, cabe nisto; o começo sai.</summary>
    public const int MaxOutputLength = 64_000;

    public const int MaxFailureLength = 2000;

    private const string OmittedHead = "… (início omitido)\n";

    private CommandExecution(
        Guid id,
        Guid taskItemId,
        Guid? taskDevelopmentId,
        Guid? developmentCommandId,
        Guid? tagDirectoryCommandId,
        string commandName,
        string commandLine,
        string workingDirectory,
        CommandMode mode,
        bool keepTerminalOpen,
        DateTimeOffset startedAt)
    {
        Id = id;
        TaskItemId = taskItemId;
        TaskDevelopmentId = taskDevelopmentId;
        DevelopmentCommandId = developmentCommandId;
        TagDirectoryCommandId = tagDirectoryCommandId;
        CommandName = commandName;
        CommandLine = commandLine;
        WorkingDirectory = workingDirectory;
        Mode = mode;
        KeepTerminalOpen = keepTerminalOpen;
        StartedAt = startedAt;
        Status = CommandExecutionStatus.Queued;
    }

    public Guid Id { get; }

    public Guid TaskItemId { get; }

    /// <summary>O ambiente. Fica <c>null</c> se ele sair da lista da tarefa; o histórico fica.</summary>
    public Guid? TaskDevelopmentId { get; private set; }

    /// <summary>O comando global. Fica <c>null</c> se ele for excluído.</summary>
    public Guid? DevelopmentCommandId { get; private set; }

    /// <summary>A associação do diretório que ofereceu o botão; <c>null</c> num comando avulso.</summary>
    public Guid? TagDirectoryCommandId { get; private set; }

    public string CommandName { get; }

    /// <summary>A linha que foi ao shell, já com variáveis e parâmetros preenchidos.</summary>
    public string CommandLine { get; }

    public string WorkingDirectory { get; }

    public CommandMode Mode { get; }

    /// <summary>
    /// Terminal com a janela aberta depois do comando (<c>cmd /k</c>): o exit
    /// code do processo é o de quem fechou a janela, e não diz nada do comando.
    /// </summary>
    public bool KeepTerminalOpen { get; private set; }

    public CommandExecutionStatus Status { get; private set; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Só no modo terminal: o processo que o app abriu.</summary>
    public int? ProcessId { get; private set; }

    /// <summary>O início do processo, que distingue o PID de um reaproveitado (ADR-030).</summary>
    public DateTimeOffset? ProcessStartedAt { get; private set; }

    /// <summary><c>null</c> quando não há um que signifique algo, como o terminal fechado pelo usuário.</summary>
    public int? ExitCode { get; private set; }

    public string? Output { get; private set; }

    public string? ErrorOutput { get; private set; }

    public string? FailureReason { get; private set; }

    public bool IsActive => Status is CommandExecutionStatus.Queued or CommandExecutionStatus.Running;

    public static CommandExecution Create(
        Guid taskItemId,
        Guid? taskDevelopmentId,
        Guid? developmentCommandId,
        Guid? tagDirectoryCommandId,
        string commandName,
        string commandLine,
        string workingDirectory,
        CommandMode mode,
        bool keepTerminalOpen,
        DateTimeOffset startedAt) =>
        new(
            Guid.CreateVersion7(startedAt),
            taskItemId,
            taskDevelopmentId,
            developmentCommandId,
            tagDirectoryCommandId,
            Clip(Required(commandName, "O comando precisa de um nome."), MaxNameLength),
            Limit(Required(commandLine, "Informe o comando."), MaxCommandLineLength, "A linha do comando"),
            Limit(Required(workingDirectory, "Informe a pasta."), MaxPathLength, "A pasta"),
            mode,
            mode == CommandMode.Terminal && keepTerminalOpen,
            startedAt);

    /// <summary>Execução escondida: o shell começou.</summary>
    public void MarkRunning()
    {
        EnsureQueued();
        Status = CommandExecutionStatus.Running;
    }

    /// <summary>Terminal: o processo existe, e é este.</summary>
    public void MarkRunning(int processId, DateTimeOffset processStartedAt)
    {
        if (processId <= 0)
        {
            throw new DomainException("O processo do terminal não tem um PID válido.");
        }

        EnsureQueued();
        ProcessId = processId;
        ProcessStartedAt = processStartedAt;
        Status = CommandExecutionStatus.Running;
    }

    /// <summary>
    /// O processo acabou. Exit code 0 ou desconhecido (terminal fechado) é
    /// concluído; o resto é falha. Terminar de novo não muda nada.
    /// </summary>
    /// <returns><c>false</c> quando a execução já tinha terminado.</returns>
    public bool Finish(int? exitCode, string? output, string? errorOutput, DateTimeOffset at)
    {
        if (!IsActive)
        {
            return false;
        }

        Status = exitCode is null or 0 ? CommandExecutionStatus.Completed : CommandExecutionStatus.Failed;
        ExitCode = exitCode;
        Close(output, errorOutput, at);

        return true;
    }

    /// <summary>O usuário cancelou: o processo foi encerrado, e o output de até ali fica.</summary>
    public bool Stop(string? output, string? errorOutput, DateTimeOffset at)
    {
        if (!IsActive)
        {
            return false;
        }

        Status = CommandExecutionStatus.Stopped;
        Close(output, errorOutput, at);

        return true;
    }

    /// <summary>Não iniciou, estourou o tempo ou se perdeu com a queda do app.</summary>
    public bool Fail(string reason, DateTimeOffset at, string? output = null, string? errorOutput = null)
    {
        if (!IsActive)
        {
            return false;
        }

        Status = CommandExecutionStatus.Failed;
        FailureReason = Clip(string.IsNullOrWhiteSpace(reason) ? "Falhou." : reason.Trim(), MaxFailureLength);
        Close(output, errorOutput, at);

        return true;
    }

    /// <summary>O ambiente saiu da lista: a execução fica no histórico da tarefa, sem ele.</summary>
    public void Unlink() => TaskDevelopmentId = null;

    private void Close(string? output, string? errorOutput, DateTimeOffset at)
    {
        Output = Tail(output);
        ErrorOutput = Tail(errorOutput);
        FinishedAt = at;
    }

    private void EnsureQueued()
    {
        if (Status != CommandExecutionStatus.Queued)
        {
            throw new DomainException("Esta execução já começou.");
        }
    }

    /// <summary>Guarda o fim: é onde estão o erro e o resultado.</summary>
    public static string? Tail(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length <= MaxOutputLength
            ? text
            : OmittedHead + text[^(MaxOutputLength - OmittedHead.Length)..];
    }

    private static string Required(string? value, string message)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? throw new DomainException(message) : trimmed;
    }

    private static string Limit(string value, int max, string label) =>
        value.Length <= max ? value : throw new DomainException($"{label} não pode passar de {max} caracteres.");

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
