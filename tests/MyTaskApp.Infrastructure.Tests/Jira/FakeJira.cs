using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using MyTaskApp.Infrastructure.Secrets;

namespace MyTaskApp.Infrastructure.Tests.Jira;

/// <summary>
/// A Atlassian em memória: responde por caminho e guarda cada pedido, com os
/// cabeçalhos, para o teste conferir o que saiu.
/// </summary>
internal sealed class FakeJiraServer : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Matches, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>Sem rede: todo pedido falha como cabo desligado.</summary>
    public bool Offline { get; set; }

    public FakeJiraServer On(string pathEnd, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add((request => request.RequestUri!.AbsolutePath.EndsWith(pathEnd, StringComparison.Ordinal), respond));
        return this;
    }

    public FakeJiraServer On(string pathEnd, HttpStatusCode status, string json = "{}") =>
        On(pathEnd, _ => Json(status, json));

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Como a rede de verdade: quem chamou recebe a Task antes da resposta.
        await Task.Yield();

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));

        if (Offline)
        {
            throw new HttpRequestException(HttpRequestError.NameResolutionError, "Host desconhecido");
        }

        // A rota registrada por último ganha: o teste pode trocar a resposta no meio.
        for (var index = _routes.Count - 1; index >= 0; index--)
        {
            if (_routes[index].Matches(request))
            {
                return _routes[index].Respond(request);
            }
        }

        return Json(HttpStatusCode.NotFound, "{}");
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

/// <summary>O cofre em memória, para os testes que não são do DPAPI.</summary>
internal sealed class InMemorySecretStore : ISecretStore
{
    public Dictionary<string, string> Secrets { get; } = [];

    public Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Secrets.GetValueOrDefault(name));

    public Task WriteAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        Secrets[name] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        Secrets.Remove(name);
        return Task.CompletedTask;
    }
}

/// <summary>Guarda tudo o que foi escrito no log, já formatado, para procurar segredo nele.</summary>
internal sealed class CapturingLog
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger<T> For<T>() => new Logger<T>(Lines);

    public bool Mentions(string text) => Lines.Any(line => line.Contains(text, StringComparison.Ordinal));

    private sealed class Logger<T>(ConcurrentQueue<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : string.Empty;

            lines.Enqueue($"{formatter(state, exception)} {values} {exception}");
        }
    }
}
