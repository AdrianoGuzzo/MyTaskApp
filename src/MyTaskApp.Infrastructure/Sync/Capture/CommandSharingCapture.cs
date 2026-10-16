using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Sync.Mapping;

namespace MyTaskApp.Infrastructure.Sync.Capture;

/// <summary>
/// O que a escolha de compartilhar um comando global (ADR-065) acrescenta a um
/// <c>SaveChanges</c>. Os botões de diretório seguem o comando, mas ninguém os
/// toca quando só o comando muda: marcar "compartilhar" precisa mandá-los
/// junto, desmarcar precisa desligá-los, e excluir um comando que não é
/// compartilhado leva os botões pela cascata do banco, sem o change tracker
/// ver — sem avisar o recorder, a varredura os acharia "sumidos" e mandaria a
/// exclusão para os outros computadores.
/// </summary>
/// <param name="Flipped">Os comandos que trocaram de compartilhado para não, ou o contrário.</param>
/// <param name="DeletedLocalOnly">Os comandos não compartilhados excluídos agora.</param>
/// <param name="UntrackedCommands">
/// Os comandos de botões tocados que não estão no tracker:
/// <see cref="SyncMappers.CommandIsLocalOnly"/> só olha o tracker.
/// </param>
internal sealed record CommandSharingCapture(
    IReadOnlyList<Guid> Flipped,
    IReadOnlyList<Guid> DeletedLocalOnly,
    IReadOnlyList<Guid> UntrackedCommands)
{
    public bool IsEmpty => Flipped.Count == 0 && DeletedLocalOnly.Count == 0 && UntrackedCommands.Count == 0;

    public static CommandSharingCapture Of(MyTaskAppDbContext context, IReadOnlyList<SyncTouched> touched)
    {
        var commands = touched
            .Where(item => item.Mapper == SyncMappers.Command && item.Entry is not null)
            .ToList();

        var flipped = commands
            .Where(item => item.Entry!.State == EntityState.Modified
                && item.Entry.Property(nameof(DevelopmentCommand.IsShared)) is { IsModified: true } property
                && !Equals(property.OriginalValue, property.CurrentValue))
            .Select(item => item.Id)
            .ToList();

        var deleted = commands
            .Where(item => item.Entry!.State == EntityState.Deleted && SyncMappers.IsLocalOnlyCommand(item.Entry))
            .Select(item => item.Id)
            .ToList();

        var untracked = touched
            .Where(item => item.Mapper == SyncMappers.DirectoryCommand && item.Entry is not null)
            .Select(item => (Guid)item.Entry!.Property(nameof(TagDirectoryCommand.DevelopmentCommandId)).CurrentValue!)
            .Distinct()
            .Where(commandId => context.DevelopmentCommands.Local.FindEntry(commandId) is null)
            .ToList();

        return new CommandSharingCapture(flipped, deleted, untracked);
    }

    public IQueryable<DevelopmentCommand> UntrackedCommandsQuery(MyTaskAppDbContext context) =>
        context.DevelopmentCommands.Where(command => UntrackedCommands.Contains(command.Id));

    /// <summary>Com tracking: o recorder lê cada botão pela entrada.</summary>
    public IQueryable<TagDirectoryCommand> FlippedLinksQuery(MyTaskAppDbContext context) =>
        context.TagDirectoryCommands.Where(link => Flipped.Contains(link.DevelopmentCommandId));

    /// <summary>Lido antes do <c>SaveChanges</c>, enquanto a cascata ainda não levou as linhas.</summary>
    public IQueryable<Guid> CascadedLinksQuery(MyTaskAppDbContext context) =>
        context.TagDirectoryCommands
            .AsNoTracking()
            .Where(link => DeletedLocalOnly.Contains(link.DevelopmentCommandId))
            .Select(link => link.Id);

    /// <summary>As entidades tocadas mais os botões que seguem os comandos.</summary>
    public List<SyncTouched> Expand(
        MyTaskAppDbContext context,
        IReadOnlyList<SyncTouched> touched,
        IReadOnlyList<Guid> cascadedLinks)
    {
        var type = SyncMappers.DirectoryCommand.Definition.Type;
        var all = touched.ToDictionary(item => (item.Mapper.Definition.Type, item.Id));

        foreach (var link in context.ChangeTracker.Entries<TagDirectoryCommand>()
                     .Where(entry => Flipped.Contains(entry.Entity.DevelopmentCommandId)))
        {
            all.TryAdd((type, link.Entity.Id), new SyncTouched(SyncMappers.DirectoryCommand, link, link.Entity.Id));
        }

        foreach (var id in cascadedLinks)
        {
            all[(type, id)] = new SyncTouched(SyncMappers.DirectoryCommand, null, id, ForceLocalOnly: true);
        }

        return [.. all.Values];
    }
}
