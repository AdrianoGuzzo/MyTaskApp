using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tags;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.QuickCommands;

/// <summary>
/// Um botão "▶" do ambiente: o comando global com o que o diretório
/// personalizou, pronto para rodar (ADR-051).
/// </summary>
/// <param name="BindingId">A associação do diretório; <c>null</c> num comando avulso.</param>
/// <param name="Template">O texto efetivo: o override do diretório, ou o global.</param>
/// <param name="WorkingDirectory">Relativa ao worktree, já com o deslocamento do diretório; <c>null</c> é a raiz.</param>
/// <param name="Parameters">Os <c>{nome}</c> que o usuário preenche, na ordem do texto.</param>
/// <param name="TagName">A etiqueta do diretório, que é o <c>{tag}</c>.</param>
public sealed record QuickCommandEntry(
    Guid CommandId,
    Guid? BindingId,
    string Name,
    string Alias,
    string Template,
    CommandMode Mode,
    string? WorkingDirectory,
    bool KeepTerminalOpen,
    bool RequiresConfirmation,
    IReadOnlyList<CommandParameterSpec> Parameters,
    string? TagName,
    string? DirectoryAlias,
    bool IsCustomized);

/// <summary>Um diretório de etiqueta que é o repositório do ambiente, ou uma pasta dentro dele.</summary>
/// <param name="Offset">A subpasta do diretório dentro do repositório, com <c>/</c>; <c>null</c> se é a raiz.</param>
public sealed record CommandDirectoryMatch(CommandDirectoryRow Directory, string? Offset);

/// <summary>
/// Quais comandos um ambiente mostra (ADR-051). Puro: recebe os diretórios e os
/// comandos globais, não pergunta ao banco.
/// </summary>
/// <remarks>
/// <para>
/// O ambiente não aponta para etiqueta nenhuma — ele guarda uma cópia do caminho
/// do repositório (ADR-027). Então vale o diretório cujo caminho é o do
/// repositório ou uma pasta dentro dele, comparado como o sistema compara
/// (<see cref="WorktreePathPlanner.SamePath"/>). Uma subpasta (<c>…\src\Eco.Web</c>)
/// roda na mesma subpasta do worktree.
/// </para>
/// <para>
/// Os diretórios das etiquetas da própria tarefa vêm primeiro; o mesmo comando
/// em dois diretórios aparece uma vez, com a configuração do primeiro.
/// </para>
/// </remarks>
public static class QuickCommandCatalog
{
    public static IReadOnlyList<CommandDirectoryMatch> Match(
        string repositoryPath,
        IReadOnlyCollection<Guid> taskTagIds,
        IReadOnlyList<CommandDirectoryRow> directories)
    {
        var matches = new List<(CommandDirectoryMatch Match, bool OnTask, int Position)>();

        for (var position = 0; position < directories.Count; position++)
        {
            var directory = directories[position];

            if (OffsetOf(repositoryPath, directory.Path) is { } offset)
            {
                matches.Add((
                    new CommandDirectoryMatch(directory, offset.Length == 0 ? null : offset),
                    taskTagIds.Contains(directory.TagId),
                    position));
            }
        }

        return
        [
            .. matches
                .OrderByDescending(match => match.OnTask)
                .ThenBy(match => match.Position)
                .Select(match => match.Match),
        ];
    }

    /// <summary>Os botões, na ordem: diretório a diretório, e dentro dele na ordem cadastrada.</summary>
    public static IReadOnlyList<QuickCommandEntry> Entries(
        IReadOnlyList<CommandDirectoryMatch> matches,
        IReadOnlyList<DevelopmentCommandRow> globals)
    {
        var entries = new List<QuickCommandEntry>();
        var seen = new HashSet<Guid>();

        foreach (var match in matches)
        {
            foreach (var binding in match.Directory.Commands.Where(binding => binding.IsEnabled).OrderBy(binding => binding.Order))
            {
                if (globals.FirstOrDefault(global => global.Id == binding.CommandId) is not { } global
                    || !seen.Add(global.Id))
                {
                    continue;
                }

                var folder = binding.WorkingDirectoryOverride is { } custom
                    ? custom == CommandWorkingDirectory.Root ? null : custom
                    : global.WorkingDirectory;

                var template = binding.CommandOverride ?? global.Command;

                entries.Add(new QuickCommandEntry(
                    global.Id,
                    binding.Id,
                    global.DisplayName,
                    global.Alias,
                    template,
                    global.Mode,
                    Combine(match.Offset, folder),
                    global.KeepTerminalOpen,
                    global.RequiresConfirmation,
                    global.ParametersOf(template),
                    match.Directory.TagName,
                    match.Directory.Alias,
                    binding.IsCustomized));
            }
        }

        return entries;
    }

    /// <summary>
    /// "+ Executar comando…": qualquer global, como cadastrado, a partir da raiz
    /// do worktree. O <c>{tag}</c> é a etiqueta do primeiro diretório que casou.
    /// </summary>
    public static QuickCommandEntry AdHoc(DevelopmentCommandRow global, IReadOnlyList<CommandDirectoryMatch> matches) =>
        new(
            global.Id,
            null,
            global.DisplayName,
            global.Alias,
            global.Command,
            global.Mode,
            global.WorkingDirectory,
            global.KeepTerminalOpen,
            global.RequiresConfirmation,
            global.ParametersOf(global.Command),
            matches.Count > 0 ? matches[0].Directory.TagName : null,
            matches.Count > 0 ? matches[0].Directory.Alias : null,
            false);

    /// <summary>
    /// A subpasta de <paramref name="directory"/> dentro de <paramref name="repository"/>,
    /// com <c>/</c>: vazia se é a mesma pasta, <c>null</c> se está fora.
    /// </summary>
    public static string? OffsetOf(string repository, string directory)
    {
        var root = WorktreePathPlanner.Canonical(repository).TrimEnd('\\', '/');
        var candidate = WorktreePathPlanner.Canonical(directory).TrimEnd('\\', '/');
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (string.Equals(root, candidate, comparison))
        {
            return string.Empty;
        }

        if (candidate.Length > root.Length + 1
            && candidate.StartsWith(root, comparison)
            && candidate[root.Length] is '\\' or '/')
        {
            return candidate[(root.Length + 1)..].Replace('\\', '/');
        }

        return null;
    }

    private static string? Combine(string? offset, string? folder) =>
        (offset, folder) switch
        {
            (null, null) => null,
            (null, _) => folder,
            (_, null) => offset,
            _ => $"{offset}/{folder}",
        };
}
