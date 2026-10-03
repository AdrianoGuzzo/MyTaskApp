using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.External;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.External;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tasks;

/// <summary>
/// Captura rápida (§36): o usuário escreve uma lista e cada linha vira uma
/// tarefa. Uma linha é um título e nada mais — não há sintaxe a decorar, que é
/// justamente o que torna o registro rápido.
/// </summary>
/// <param name="TagIds">
/// Etiquetas escolhidas no botão da caixa de captura, aplicadas a todas as
/// linhas (ADR-025). Escolha na tela, e não "#etiqueta" no texto: seria sintaxe
/// a decorar, e um "#1" num título viraria etiqueta sem ninguém pedir.
/// </param>
/// <param name="Links">
/// As issues escolhidas no autocomplete do Jira (ADR-045). A linha que começa
/// com a chave de uma delas — <c>GAECO-1234 Corrigir erro</c> — nasce
/// vinculada, e a chave sai do título. A chave fica visível na caixa enquanto
/// se escreve, então apagá-la é desfazer o vínculo, sem botão nenhum. Uma
/// chave que ninguém escolheu continua texto: a captura não vai à rede.
/// </param>
public sealed record QuickCapture(
    string Text,
    IReadOnlyCollection<Guid>? TagIds = null,
    IReadOnlyCollection<ExternalTask>? Links = null);

public sealed record QuickCaptureResult(IReadOnlyList<Guid> TaskIds)
{
    public int Count => TaskIds.Count;
}

public sealed class QuickCaptureHandler(
    ITaskItemRepository tasks,
    ITagRepository tags,
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

        // Validadas antes de criar qualquer coisa, pelo mesmo tudo-ou-nada.
        var tagIds = await tags.GetExistingAsync(command.TagIds, cancellationToken);

        // Uma leitura do padrao para o lote inteiro, nao uma por linha.
        var reminder = (await settings.GetAsync(cancellationToken)).DefaultPolicy;

        var created = titles
            .Select(line => Create(line, command.Links, createdAt, today, reminder))
            .ToList();

        foreach (var task in created)
        {
            task.SetTags(tagIds);
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
    /// A tarefa da linha, vinculada se ela começar com a chave de uma issue
    /// escolhida. O título é o que vem depois da chave; sem nada depois, o da
    /// issue.
    /// </summary>
    private static TaskItem Create(
        string line,
        IReadOnlyCollection<ExternalTask>? links,
        DateTimeOffset createdAt,
        TaskSchedule today,
        Domain.Reminders.ReminderPolicy reminder)
    {
        if (links is not { Count: > 0 } || !IssueKey.TryParsePrefix(line, out var key, out var rest))
        {
            return TaskItem.Create(line, createdAt, schedule: today, reminder: reminder);
        }

        var issue = links.FirstOrDefault(link => IssueKey.TryParse(link.Id, out var linkKey) && linkKey == key);

        if (issue is null)
        {
            return TaskItem.Create(line, createdAt, schedule: today, reminder: reminder);
        }

        var title = rest.Length > 0 ? rest : ExternalLink.TaskTitleFor(issue.Title);
        var task = TaskItem.Create(title, createdAt, schedule: today, reminder: reminder);
        task.LinkExternal(issue.ToLink(createdAt));

        return task;
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
