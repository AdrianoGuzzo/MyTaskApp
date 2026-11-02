using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Terminals;

namespace MyTaskApp.Infrastructure.Tests.Terminals;

/// <summary>O agente ouvindo o canal local do terminal (ADR-098): um nome por banco, várias vistas ao mesmo tempo.</summary>
public sealed class TerminalPipeListenerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheName_ComesFromTheDatabase_SoIsolatedInstallsDoNotMix()
    {
        var one = TerminalPipe.NameFor(Path.Combine(Path.GetTempPath(), "a", "mytaskapp.db"));

        one.Should().StartWith("MyTaskApp.Terminal.");
        TerminalPipe.NameFor(Path.Combine(Path.GetTempPath(), "a", "mytaskapp.db")).Should().Be(one);
        TerminalPipe.NameFor(Path.Combine(Path.GetTempPath(), "b", "mytaskapp.db")).Should().NotBe(one);
    }

    [Fact]
    public async Task WithoutTheAgent_ConnectingSaysWhatHappened()
    {
        var act = () => TerminalPipe.ConnectAsync($"MyTaskApp.Terminal.{Guid.NewGuid():N}", Ct);

        (await act.Should().ThrowAsync<DomainException>()).Which.Message.Should().Contain("MyTaskApp Agent");
    }

    [Fact]
    public async Task SeveralViews_AreServedAtOnce_AndStoppingClosesThem()
    {
        var name = $"MyTaskApp.Terminal.{Guid.NewGuid():N}";
        var server = new LocalTerminalServer(NoTerminalSessions.Instance, new NoUseCases(), NullLogger<LocalTerminalServer>.Instance);
        var listener = new TerminalPipeListener(name, server, NullLogger<TerminalPipeListener>.Instance);
        listener.Start();

        try
        {
            // Duas vistas de uma sessão que não existe: cada uma recebe a própria recusa.
            await using var first = await LocalTerminalChannel.AttachAsync(await TerminalPipe.ConnectAsync(name, Ct), Guid.NewGuid(), TerminalClientKind.NativeWindow, "Janela", null, Ct);
            await using var second = await LocalTerminalChannel.AttachAsync(await TerminalPipe.ConnectAsync(name, Ct), Guid.NewGuid(), TerminalClientKind.Embedded, "App", null, Ct);

            (await first.ReadAsync(Ct)).Should().BeOfType<TerminalRefusal>();
            (await second.ReadAsync(Ct)).Should().BeOfType<TerminalRefusal>();
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        var afterwards = () => TerminalPipe.ConnectAsync(name, Ct);
        await afterwards.Should().ThrowAsync<DomainException>("parado, o agente não atende mais");
    }

    /// <summary>
    /// Vistas que chegam juntas são todas atendidas. No Linux, sem uma instância
    /// de reserva, quem conectava entre um aceite e o próximo levava
    /// "connection reset".
    /// </summary>
    [Fact]
    public async Task ViewsArrivingTogether_AreAllAnswered()
    {
        var name = $"MyTaskApp.Terminal.{Guid.NewGuid():N}";
        var server = new LocalTerminalServer(NoTerminalSessions.Instance, new NoUseCases(), NullLogger<LocalTerminalServer>.Instance);
        await using var listener = new TerminalPipeListener(name, server, NullLogger<TerminalPipeListener>.Instance);
        listener.Start();

        for (var round = 0; round < 25; round++)
        {
            var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            {
                await using var view = await LocalTerminalChannel.AttachAsync(await ConnectPatientlyAsync(name), Guid.NewGuid(), TerminalClientKind.Embedded, "App", null, Ct);
                return await view.ReadAsync(Ct);
            }));

            answers.Should().AllBeOfType<TerminalRefusal>();
        }
    }

    /// <summary>
    /// O mesmo pipe do <see cref="TerminalPipe.ConnectAsync"/>, com prazo de
    /// teste: no runner carregado do CI, os 2 s do agente não bastam para cem
    /// conexões. O que o teste cobra é a resposta, não a pressa.
    /// </summary>
    private static async Task<Stream> ConnectPatientlyAsync(string name)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(TimeSpan.FromSeconds(30), Ct);
        return client;
    }

    /// <summary>Só a vista é exercitada aqui: nenhum caso de uso roda.</summary>
    private sealed class NoUseCases : IUseCaseRunner
    {
        public Task<TResult> RunAsync<THandler, TResult>(Func<THandler, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
            where THandler : notnull =>
            throw new InvalidOperationException("Nenhum caso de uso neste teste.");

        public Task RunAsync<THandler>(Func<THandler, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
            where THandler : notnull =>
            throw new InvalidOperationException("Nenhum caso de uso neste teste.");
    }
}
