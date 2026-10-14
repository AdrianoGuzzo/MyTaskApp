using Microsoft.Extensions.Logging;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Guarda o que o ViewModel registrou. O nome do evento é o que se procura no
/// log quando alguém conta que "deu erro": precisa continuar o mesmo.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception), exception));
}
