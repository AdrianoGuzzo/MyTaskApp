using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Consultations;

namespace MyTaskApp.Mcp.Tests;

/// <summary>
/// A Consulta Inteligente com o Claude Code de verdade (ADR-081): os
/// argumentos que o app gera, o servidor MCP do app no ar e o segredo da
/// sessão só no ambiente. Prova o que nenhum fake prova — que o Claude lê o
/// arquivo de instruções, expande o segredo, carrega as ferramentas de
/// consulta e fica sem Bash nem Edit.
/// </summary>
/// <remarks>
/// Opcional: só com <c>MYTASKAPP_TEST_CLAUDE=1</c>, o Claude instalado e com
/// login. Chama o modelo (haiku), então custa alguns centavos e leva alguns
/// segundos. O banco é o falso de <see cref="ConsultationToolTests"/>.
/// </remarks>
public class ConsultationClaudeTests
{
    public const string Variable = "MYTASKAPP_TEST_CLAUDE";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheRealClaude_ReadsTheInstructions_UsesTheConsultationTools_AndCannotRunCommands()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(Variable) == "1",
            $"Defina {Variable}=1 para abrir o Claude Code de verdade (chama o modelo).");

        await using var host = await McpTestHost.CreateAsync(
            cancellationToken: Ct,
            configure: services => services.AddSingleton<IConsultationDatabase>(new RecordingConsultationDatabase()));

        var provider = host.Services.GetRequiredService<IAgentCliProviders>().Default;
        var detection = await provider.DetectAsync(Ct);
        Assert.SkipUnless(detection.IsInstalled, "O Claude Code não está instalado neste computador.");

        var (token, sessionId) = await ConsultationToolTests.OpenConsultationAsync(host);
        var folder = Directory.CreateTempSubdirectory("mytaskapp-consulta-").FullName;
        File.WriteAllText(Path.Combine(folder, "LEIA.md"), "Este projeto é um teste da Consulta Inteligente.");

        var launch = ((IConsultationLaunchBuilder)provider).CreateConsultationLaunch(
            new ConsultationLaunchRequest(
                sessionId,
                Guid.NewGuid().ToString(),
                false,
                "Consulta · teste",
                [folder],
                "Palavra de controle: ABACAXI-42. Responda sempre em português.",
                "1) Qual é a palavra de controle? 2) Chame consultation_database_describe e diga o nome do banco. "
                + "3) Liste, uma por linha, os nomes exatos de todas as ferramentas que você tem.",
                new ConsultationToolsAccess(host.Endpoint, ConsultationToolNames.TokenVariable, ConsultationToolNames.All),
                ["--model", "haiku"],
                new Dictionary<string, string> { [ConsultationToolNames.TokenVariable] = token },
                null),
            detection);

        try
        {
            var output = await RunAsync(launch);

            output.Should().Contain("ABACAXI-42", "o arquivo de instruções foi lido");
            output.Should().Contain("loja_copia", "o segredo foi expandido e a ferramenta de consulta respondeu");
            output.Should().Contain(ConsultationToolNames.Query, "as ferramentas de consulta carregam junto com --tools");
            output.Should().NotContain("Bash").And.NotContain("PowerShell");
        }
        finally
        {
            foreach (var file in launch.Arguments.Where(File.Exists))
            {
                File.Delete(file);
            }

            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>O mesmo comando, com <c>-p</c>: responde e sai, sem terminal.</summary>
    private static async Task<string> RunAsync(TerminalLaunchOptions launch)
    {
        var start = new ProcessStartInfo(launch.Executable)
        {
            WorkingDirectory = launch.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add("-p");

        foreach (var argument in launch.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in launch.Environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));

        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        return await output + await error;
    }
}
