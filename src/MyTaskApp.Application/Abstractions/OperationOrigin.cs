namespace MyTaskApp.Application.Abstractions;

/// <summary>
/// De onde veio a operação em curso, quando não foi da tela (ADR-059). A
/// auditoria grava "adria (MCP)" em vez de só "adria": quem lê a trilha precisa
/// saber que foi a IA que concluiu a tarefa, e não a pessoa.
/// </summary>
/// <remarks>
/// <see cref="AsyncLocal{T}"/>, e não parâmetro: o autor já chega aos casos de
/// uso pelo <see cref="ICurrentUser"/>, e trocar a assinatura de todos eles para
/// carregar a origem seria reescrever o caminho que a tela usa. O valor segue a
/// chamada assíncrona e não vaza para outras — duas requisições ao mesmo tempo
/// não se misturam.
/// </remarks>
public static class OperationOrigin
{
    private static readonly AsyncLocal<string?> Ambient = new();

    /// <summary><c>null</c> = a própria tela.</summary>
    public static string? Current => Ambient.Value;

    /// <summary>Marca a origem até o <see cref="IDisposable.Dispose"/>.</summary>
    public static IDisposable Enter(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        var previous = Ambient.Value;
        Ambient.Value = origin;

        return new Restore(previous);
    }

    /// <summary>O nome do autor com a origem, se houver: "adria (MCP)", ou "MCP" sem nome.</summary>
    public static string? Describe(string? name) =>
        Current is not { } origin
            ? name
            : name is null ? origin : $"{name} ({origin})";

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
