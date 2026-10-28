using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Agent.Tests;

/// <summary>
/// A avaliação do processo vivo (fase 3, E6): um <c>claude -p</c> que fica
/// aberto lendo mensagens do stdin (<c>--input-format stream-json</c>) e
/// devolve cada uma no stdout (<c>--replay-user-messages</c>) como recibo de
/// entrega. Não é o caminho do agente, que roda um processo por prompt com a
/// fila por conversa; este teste só guarda o que a interface oficial faz, para
/// a decisão de adotar ou não.
/// </summary>
/// <remarks>
/// Opcional: só com <c>MYTASKAPP_TEST_CLAUDE=1</c>, o Claude instalado e
/// logado (chama o haiku). Sem ferramentas: nenhum perfil de permissão muda.
/// </remarks>
public sealed class LiveProcessClaudeTests : IAsyncDisposable
{
    private const string Variable = "MYTASKAPP_TEST_CLAUDE";

    private readonly AgentTestHost _host = AgentTestHost.Create();

    private readonly string _folder = Directory.CreateTempSubdirectory("mytaskapp-live-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task TheLiveProcess_EchoesEachMessage_AndAnswersBothInTheSameSession()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(Variable) == "1", $"Defina {Variable}=1 para rodar o Claude Code de verdade.");
        var detection = await _host.Services.GetRequiredService<IHeadlessAgentProvider>().DetectAsync(Ct);
        Assert.SkipUnless(detection.IsInstalled && detection.ExecutablePath is not null, "O Claude Code não está instalado neste computador.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var start = new ProcessStartInfo(detection.ExecutablePath!)
        {
            WorkingDirectory = _folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in new[]
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--replay-user-messages", "--model", "haiku", "--max-turns", "1", "--tools", "",
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;

        try
        {
            var first = await SendAsync(process, "Responda só com a palavra UM.", timeout.Token);
            Assert.SkipWhen(first.Result.GetProperty("is_error").GetBoolean(), "O Claude precisa estar logado (claude auth login).");
            var second = await SendAsync(process, "Agora responda só com a palavra DOIS.", timeout.Token);

            first.Echoed.Should().BeTrue("a mensagem volta como recibo antes da resposta");
            second.Echoed.Should().BeTrue();
            first.Result.GetProperty("result").GetString().Should().Contain("UM");
            second.Result.GetProperty("result").GetString().Should().Contain("DOIS");
            second.Result.GetProperty("session_id").GetString().Should().Be(first.Result.GetProperty("session_id").GetString(), "o mesmo processo, a mesma conversa");
        }
        finally
        {
            process.StandardInput.Close();

            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>Manda uma mensagem e lê até o resultado dela, notando se ela voltou como recibo.</summary>
    private static async Task<(bool Echoed, JsonElement Result)> SendAsync(Process process, string text, CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = text } });
        await process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        var echoed = false;

        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0 || line[0] != '{')
            {
                continue;
            }

            var root = JsonDocument.Parse(line).RootElement;

            switch (root.GetProperty("type").GetString())
            {
                case "user" when line.Contains(text, StringComparison.Ordinal):
                    echoed = true;
                    break;
                case "result":
                    return (echoed, root.Clone());
            }
        }

        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        Assert.SkipWhen(
            error.Contains("authenticat", StringComparison.OrdinalIgnoreCase) || error.Contains("login", StringComparison.OrdinalIgnoreCase),
            "O Claude precisa estar logado (claude auth login).");
        Assert.Fail($"O Claude saiu sem responder: {error}");
        return default;
    }
}
