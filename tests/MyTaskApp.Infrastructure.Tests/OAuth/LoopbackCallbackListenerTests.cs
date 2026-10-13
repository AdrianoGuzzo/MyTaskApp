using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.OAuth;

namespace MyTaskApp.Infrastructure.Tests.OAuth;

/// <summary>
/// O listener de volta da autorização, compartilhado entre o Jira e a
/// sincronização (ADR-061): cada provedor fala o próprio nome.
/// </summary>
public sealed class LoopbackCallbackListenerTests
{
    private static readonly LoopbackCallbackMessages Messages = new(
        PortInUse: "Porta {port} ocupada pelo Teste.",
        Connected: "Conectado ao Teste.",
        NotCompleted: "Não concluída.",
        Denied: "Recusada no Teste.",
        Failed: "Falhou no Teste.",
        ReceivedLogEvent: "TestCallbackReceived");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WaitForCode_WithTheExpectedState_ReturnsTheCodeAndShowsTheProviderPage()
    {
        await using var listener = LoopbackCallbackListener.Start(0, Messages, NullLogger.Instance);
        using var browser = new HttpClient();

        var waiting = listener.WaitForCodeAsync("abc", Ct);
        var page = await browser.GetStringAsync(Callback(listener, "state=abc&code=xyz"), Ct);

        (await waiting).Should().Be("xyz");
        page.Should().Contain("Conectado ao Teste.");
    }

    [Fact]
    public async Task WaitForCode_WithAnotherState_IgnoresItAndKeepsWaiting()
    {
        await using var listener = LoopbackCallbackListener.Start(0, Messages, NullLogger.Instance);
        using var browser = new HttpClient();

        var waiting = listener.WaitForCodeAsync("abc", Ct);
        using var stranger = await browser.GetAsync(Callback(listener, "state=other&code=evil"), Ct);

        stranger.StatusCode.Should().Be(HttpStatusCode.NotFound);
        waiting.IsCompleted.Should().BeFalse();

        await browser.GetStringAsync(Callback(listener, "state=abc&code=good"), Ct);
        (await waiting).Should().Be("good");
    }

    [Theory]
    [InlineData("access_denied", "Recusada no Teste.")]
    [InlineData("server_error", "Falhou no Teste.")]
    public async Task WaitForCode_WhenTheProviderRefuses_ThrowsTheProviderMessage(string error, string message)
    {
        await using var listener = LoopbackCallbackListener.Start(0, Messages, NullLogger.Instance);
        using var browser = new HttpClient();

        var waiting = listener.WaitForCodeAsync("abc", Ct);
        var page = await browser.GetStringAsync(Callback(listener, $"state=abc&error={error}"), Ct);

        WebUtility.HtmlDecode(page).Should().Contain("Não concluída.");
        (await waiting.Awaiting(task => task).Should().ThrowAsync<DomainException>()).Which.Message.Should().Be(message);
    }

    [Fact]
    public void Start_OnABusyPort_SaysWhichPortAndWhichProvider()
    {
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();

        try
        {
            var port = ((IPEndPoint)busy.LocalEndpoint).Port;

            var start = () => LoopbackCallbackListener.Start(port, Messages, NullLogger.Instance);

            start.Should().Throw<DomainException>().WithMessage($"Porta {port} ocupada pelo Teste.");
        }
        finally
        {
            busy.Stop();
        }
    }

    private static Uri Callback(LoopbackCallbackListener listener, string query) =>
        new($"http://127.0.0.1:{listener.Port}{LoopbackCallbackListener.CallbackPath}?{query}");
}
