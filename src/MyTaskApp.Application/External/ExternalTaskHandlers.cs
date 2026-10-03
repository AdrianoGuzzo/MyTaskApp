using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain;
using MyTaskApp.Domain.External;

namespace MyTaskApp.Application.External;

// Os casos de uso da tarefa vinculada (ADR-045). Nenhum deles é necessário para
// a tarefa funcionar: sem Jira, ela continua abrindo, com worktree, terminal e
// agente — só o que precisa da rede fica indisponível.

/// <summary>O autocomplete pergunta. Passa pelo cache compartilhado.</summary>
public sealed record SearchExternalTasks(string Query);

public sealed class SearchExternalTasksHandler(ExternalTaskSearch search)
{
    public Task<ExternalTaskSearchResult> HandleAsync(
        SearchExternalTasks query,
        CancellationToken cancellationToken = default) =>
        search.SearchAsync(query.Query, cancellationToken);
}

/// <summary>
/// O vínculo da tarefa e o nome de branch que ele sugere. A sugestão vai para
/// a aba Desenvolvimento no lugar de <c>feature/{slug-do-título}</c>.
/// </summary>
public sealed record GetTaskExternalContext(Guid TaskId);

/// <param name="SuggestedBranch"><c>null</c> = tarefa só local: vale a sugestão de sempre.</param>
public sealed record TaskExternalContext(ExternalLink? Link, string? SuggestedBranch);

public sealed class GetTaskExternalContextHandler(ITaskItemRepository tasks, IBranchConventionStore conventions)
{
    public async Task<TaskExternalContext> HandleAsync(
        GetTaskExternalContext query,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(query.TaskId, cancellationToken);

        if (task.External is not { } link)
        {
            return new TaskExternalContext(null, null);
        }

        var strategy = new ConventionBranchNameStrategy(await conventions.GetAsync(cancellationToken));

        return new TaskExternalContext(link, strategy.GenerateBranchName(ExternalTask.From(link)));
    }
}

/// <summary>Liga uma tarefa que já existe a uma issue. O título dela fica como o usuário escreveu.</summary>
public sealed record LinkTaskToExternal(Guid TaskId, ExternalTask Issue);

public sealed class LinkTaskToExternalHandler(ITaskItemRepository tasks, IUnitOfWork unitOfWork, TimeProvider timeProvider)
{
    public async Task<ExternalLink> HandleAsync(LinkTaskToExternal command, CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);
        var link = command.Issue.ToLink(timeProvider.GetUtcNow());

        task.LinkExternal(link);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return link;
    }
}

public sealed record UnlinkTaskFromExternal(Guid TaskId);

public sealed class UnlinkTaskFromExternalHandler(ITaskItemRepository tasks, IUnitOfWork unitOfWork)
{
    public async Task HandleAsync(UnlinkTaskFromExternal command, CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        task.UnlinkExternal();
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// "Atualizar do Jira": título, tipo e status como estão agora. Manual, de
/// propósito — nada de polling (ADR-045). Sem rede, o retrato fica como estava
/// e a mensagem diz por quê.
/// </summary>
public sealed record RefreshExternalTask(Guid TaskId);

public sealed class RefreshExternalTaskHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    ExternalTaskSearch search,
    TimeProvider timeProvider)
{
    public async Task<ExternalLink> HandleAsync(RefreshExternalTask command, CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        var current = task.External
            ?? throw new DomainException("Esta tarefa não está vinculada a nenhuma issue.");

        ExternalTask? fresh;

        try
        {
            fresh = await search.GetAsync(current.Provider, current.Id, cancellationToken);
        }
        catch (ExternalTaskUnavailableException exception)
        {
            throw new DomainException($"{exception.Message} A tarefa continua com o que foi lido antes.");
        }

        if (fresh is null)
        {
            throw new DomainException(
                $"A issue {current.Id} não foi encontrada no {current.Provider}. "
                + "Ela pode ter sido excluída ou movida, ou sua conta perdeu o acesso.");
        }

        var link = fresh.ToLink(timeProvider.GetUtcNow());

        task.RefreshExternal(link);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return link;
    }
}

/// <summary>As convenções de branch como texto, uma linha por tipo.</summary>
public sealed record GetBranchConventions;

public sealed class GetBranchConventionsHandler(IBranchConventionStore conventions)
{
    public async Task<string> HandleAsync(GetBranchConventions query, CancellationToken cancellationToken = default) =>
        (await conventions.GetAsync(cancellationToken)).ToText();
}

/// <summary>Texto em branco devolve as convenções de fábrica.</summary>
public sealed record UpdateBranchConventions(string? Text);

public sealed class UpdateBranchConventionsHandler(IBranchConventionStore conventions)
{
    public async Task HandleAsync(UpdateBranchConventions command, CancellationToken cancellationToken = default)
    {
        var parsed = string.IsNullOrWhiteSpace(command.Text)
            ? BranchConventions.Default
            : BranchConventions.Parse(command.Text);

        await conventions.SaveAsync(parsed, cancellationToken);
    }
}
