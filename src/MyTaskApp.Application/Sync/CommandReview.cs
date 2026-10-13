using MyTaskApp.Application.Commands;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Application.Sync;

/// <summary>Uma entidade sincronizada de onde sai texto de comando.</summary>
public sealed record CommandSource(string EntityType, Guid EntityId);

/// <summary>
/// Texto de comando que veio de outro computador não roda aqui sem alguém
/// olhar antes (ADR-060). Uma conta invadida poderia mandar um comando para
/// todas as máquinas do usuário; este portão é o que impede isso.
/// </summary>
public interface ICommandReviewGate
{
    /// <summary>Recusa com <see cref="CommandReviewRequiredException"/> se algum ainda não foi revisto.</summary>
    Task EnsureReviewedAsync(IReadOnlyCollection<CommandSource> sources, CancellationToken cancellationToken);
}

/// <summary>A recusa: a frase diz o que fazer, e a tela a mostra como qualquer regra.</summary>
public sealed class CommandReviewRequiredException(string message) : DomainException(message);

/// <summary>Sem sincronização, nada veio de fora: tudo passa.</summary>
internal sealed class NoCommandReviewGate : ICommandReviewGate
{
    public Task EnsureReviewedAsync(IReadOnlyCollection<CommandSource> sources, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>De onde vem o texto de uma execução: os globais que as entradas chamam por <c>@alias</c>.</summary>
internal static class CommandReviewSources
{
    public static IEnumerable<CommandSource> GlobalsUsedBy(IEnumerable<string> entries, IEnumerable<DevelopmentCommand> commands)
    {
        var aliases = entries
            .Select(CommandAliasResolver.AliasOf)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return commands
            .Where(command => command.Alias is { } alias && aliases.Contains(alias))
            .Select(command => new CommandSource(SyncEntityTypes.Command, command.Id));
    }
}
