using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Domain;

namespace MyTaskApp.Mcp.Hosting;

/// <summary>
/// Por onde toda ferramenta passa (ADR-059). Não tem regra de negócio: chama o
/// caso de uso pelo <see cref="IUseCaseRunner"/> — um escopo e um DbContext por
/// operação, como a tela — e cuida só do que é da porta:
/// <list type="bullet">
/// <item>recusa escrita no modo somente leitura;</item>
/// <item>uma operação MCP por vez, porque o SQLite do app não tem WAL e a tela também grava;</item>
/// <item>a origem "MCP" na auditoria;</item>
/// <item><see cref="DomainException"/> vira a mensagem da regra; o resto vira um código, e o detalhe fica no log;</item>
/// <item>avisa a tela do que mudou.</item>
/// </list>
/// Uma Consulta Inteligente (ADR-081) só passa por <see cref="ConsultAsync"/>:
/// as outras portas recusam o segredo dela, mesmo que um filtro falhe.
/// </summary>
public sealed class McpGateway(
    IUseCaseRunner runner,
    IDataChangeNotifier notifier,
    McpServerRuntime runtime,
    McpActivityLog activity,
    TimeProvider timeProvider,
    ILogger<McpGateway> logger)
{
    /// <summary>A origem gravada na auditoria: "adria (MCP)".</summary>
    public const string Origin = "MCP";

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public Task<T> ReadAsync<T>(
        string operation,
        Func<IUseCaseRunner, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        RefuseConsultation(operation);
        return RunAsync(operation, DataArea.None, read, cancellationToken, exclusive: true);
    }

    /// <summary>
    /// Uma ferramenta da Consulta Inteligente, para a sessão que chamou. Fora
    /// da fila de uma operação por vez: do SQLite do app ela só lê, e a parte
    /// demorada é no PostgreSQL — uma consulta de 15 s não segura o resto do
    /// MCP.
    /// </summary>
    public Task<T> ConsultAsync<T>(
        string operation,
        Func<IUseCaseRunner, ConsultationAccess, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        if (McpCaller.Current is not { } access)
        {
            activity.Add(new McpActivityEntry(
                timeProvider.GetUtcNow(), operation, McpActivityOutcome.Denied, TimeSpan.Zero, "Fora de uma Consulta Inteligente."));

            throw new McpException(
                "Esta ferramenta só existe numa sessão de Consulta Inteligente, aberta pela aba da tarefa no MyTaskApp.");
        }

        return RunAsync(operation, DataArea.None, (runner, token) => read(runner, access, token), cancellationToken, exclusive: false);
    }

    public Task<T> WriteAsync<T>(
        string operation,
        DataArea changes,
        Func<IUseCaseRunner, CancellationToken, Task<T>> write,
        CancellationToken cancellationToken)
    {
        RefuseConsultation(operation);

        if (runtime.ReadOnly)
        {
            activity.Add(new McpActivityEntry(
                timeProvider.GetUtcNow(), operation, McpActivityOutcome.Denied, TimeSpan.Zero, "Somente leitura."));

            throw new McpException(
                "O servidor MCP do MyTaskApp está em modo somente leitura. " +
                "Para permitir alterações, desligue \"Somente leitura\" na tela do servidor MCP.");
        }

        return RunAsync(operation, changes == DataArea.None ? DataArea.Tasks : changes, write, cancellationToken, exclusive: true);
    }

    /// <summary>O segredo de uma consulta abre só as ferramentas de consulta.</summary>
    private void RefuseConsultation(string operation)
    {
        if (McpCaller.Current is null)
        {
            return;
        }

        activity.Add(new McpActivityEntry(
            timeProvider.GetUtcNow(), operation, McpActivityOutcome.Denied, TimeSpan.Zero, "Consulta Inteligente."));

        throw new McpException("Numa Consulta Inteligente, só as ferramentas de consulta do banco estão disponíveis.");
    }

    private async Task<T> RunAsync<T>(
        string operation,
        DataArea changes,
        Func<IUseCaseRunner, CancellationToken, Task<T>> run,
        CancellationToken cancellationToken,
        bool exclusive)
    {
        var watch = Stopwatch.StartNew();

        if (exclusive)
        {
            await _oneAtATime.WaitAsync(cancellationToken);
        }

        try
        {
            using var origin = OperationOrigin.Enter(Origin);

            var result = await run(runner, cancellationToken);

            Record(operation, McpActivityOutcome.Succeeded, watch.Elapsed, null);
            logger.LogInformation("McpToolInvoked {Tool} {Outcome} {ElapsedMs}", operation, "Succeeded", watch.ElapsedMilliseconds);

            if (changes != DataArea.None)
            {
                Notify(changes);
            }

            return result;
        }
        catch (DomainException exception)
        {
            // A frase da regra é feita para o usuário ler; é ela que a IA recebe.
            Record(operation, McpActivityOutcome.Rejected, watch.Elapsed, exception.Message);
            logger.LogInformation("McpToolRejected {Tool} {Reason} {ElapsedMs}", operation, exception.Message, watch.ElapsedMilliseconds);

            throw new McpException(exception.Message);
        }
        catch (McpException exception)
        {
            Record(operation, McpActivityOutcome.Rejected, watch.Elapsed, exception.Message);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Record(operation, McpActivityOutcome.Rejected, watch.Elapsed, "Cancelado pelo cliente.");
            throw;
        }
        catch (Exception exception)
        {
            // Nada de pilha nem mensagem interna para fora: um código que liga a
            // resposta ao log, onde o detalhe está.
            var code = Guid.NewGuid().ToString("N")[..8];

            Record(operation, McpActivityOutcome.Failed, watch.Elapsed, $"Erro interno ({code}): {exception.GetType().Name}.");
            logger.LogError(exception, "McpToolFailed {Tool} {ErrorCode}", operation, code);

            throw new McpException($"Erro interno no MyTaskApp (código {code}). O detalhe está no log do app.");
        }
        finally
        {
            if (exclusive)
            {
                _oneAtATime.Release();
            }
        }
    }

    private void Record(string operation, McpActivityOutcome outcome, TimeSpan elapsed, string? detail) =>
        activity.Add(new McpActivityEntry(timeProvider.GetUtcNow(), operation, outcome, elapsed, detail));

    private void Notify(DataArea changes)
    {
        try
        {
            notifier.NotifyChanged(changes);
        }
        catch (Exception exception)
        {
            // A gravação já aconteceu: uma tela que falhou ao recarregar não a desfaz.
            logger.LogWarning(exception, "McpChangeNotificationFailed {Areas}", changes);
        }
    }
}
