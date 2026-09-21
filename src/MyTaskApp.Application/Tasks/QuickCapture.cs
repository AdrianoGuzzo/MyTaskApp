using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tasks;

/// <summary>
/// Captura rápida (§36): o usuário escreve uma lista e cada linha vira uma
/// tarefa. Uma linha é um título e nada mais — não há sintaxe a decorar, que é
/// justamente o que torna o registro rápido.
/// </summary>
public sealed record QuickCapture(string Text);

public sealed record QuickCaptureResult(IReadOnlyList<Guid> TaskIds)
{
    public int Count => TaskIds.Count;
}

public sealed class QuickCaptureHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IReminderSettingsStore settings,
    ITaskAuditLog audit,
    ICurrentUser currentUser,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<QuickCaptureHandler> logger)
{
    /// <summary>
    /// Teto para o dedo escorregar no Ctrl+V: colar um documento inteiro criaria
    /// centenas de tarefas que ninguém desfaz uma a uma. Recusar é mais barato.
    /// </summary>
    public const int MaxLines = 100;

    public async Task<QuickCaptureResult> HandleAsync(
        QuickCapture command,
        CancellationToken cancellationToken = default)
    {
        var titles = SplitLines(command.Text);

        if (titles.Count == 0)
        {
            throw new DomainException("Escreva pelo menos uma tarefa.");
        }

        if (titles.Count > MaxLines)
        {
            throw new DomainException(
                $"São linhas demais de uma vez: o limite é {MaxLines} por captura.");
        }

        // A lista inteira é criada — e validada — antes de qualquer gravação: uma
        // linha recusada no meio não pode deixar metade do checklist no banco.
        var createdAt = timeProvider.GetUtcNow();
        var today = TaskSchedule.On(clock.Today);

        // Uma leitura do padrao para o lote inteiro, nao uma por linha.
        var reminder = (await settings.GetAsync(cancellationToken)).DefaultPolicy;

        var created = titles
            .Select(title => TaskItem.Create(title, createdAt, schedule: today, reminder: reminder))
            .ToList();

        foreach (var task in created)
        {
            ReminderArming.Arm(task, task.Occurrences.Single(), clock, createdAt);
            await tasks.AddAsync(task, cancellationToken);

            await audit.RecordAsync(
                TaskAuditEntry.ByUser(
                    task.Id,
                    task.Title,
                    TaskAuditOperation.Created,
                    createdAt,
                    currentUser.Name),
                cancellationToken);
        }

        // Um único SaveChanges: ou o checklist entra inteiro, ou não entra nada.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("QuickCaptured {Count} {Date}", created.Count, clock.Today);

        return new QuickCaptureResult([.. created.Select(task => task.Id)]);
    }

    /// <summary>
    /// Quebra por linha aceitando CRLF, LF e CR — o texto pode vir de qualquer
    /// lugar. Linhas em branco são ignoradas em silêncio; o usuário que separou
    /// itens com uma linha vazia não quis criar uma tarefa sem nome.
    /// </summary>
    private static List<string> SplitLines(string? text) =>
    [
        .. (text ?? string.Empty).Split(
            ['\r', '\n'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
    ];
}
