namespace MyTaskApp.Desktop.Notes;

/// <summary>
/// O que vem depois do <c>@</c> no texto do agente (ADR-039): só a busca
/// (<c>@tod</c>), ou a chave de um ambiente e o caminho dentro dele
/// (<c>@MyTaskApp/src/tod</c>).
/// </summary>
/// <param name="EnvironmentKey">A chave antes da primeira barra; <c>null</c> sem barra.</param>
/// <param name="Rest">Depois da barra, ou a busca inteira sem ela. Barras sempre <c>/</c>.</param>
public readonly record struct ReferenceQuery(string? EnvironmentKey, string Rest)
{
    public string Text => EnvironmentKey is null ? Rest : $"{EnvironmentKey}/{Rest}";
}

/// <summary>Um arquivo ou pasta do índice, relativo ao worktree e com <c>/</c>.</summary>
public readonly record struct PathEntry(string Path, bool IsDirectory, int NameStart)
{
    public string Name => Path[NameStart..];

    /// <summary>A pasta de cima, relativa; vazia na raiz.</summary>
    public string Parent => NameStart == 0 ? string.Empty : Path[..(NameStart - 1)];
}

/// <summary>Um resultado da busca, com a nota que o ordenou.</summary>
public readonly record struct PathMatch(PathEntry Entry, int Score);

/// <summary>
/// A aritmética da referência por <c>@</c> no texto do agente (ADR-039), sem
/// controle nenhum — testada como <see cref="AliasCompletion"/>.
/// </summary>
/// <remarks>
/// A busca é "fuzzy" no estilo do Ctrl+P dos editores: as letras digitadas
/// precisam aparecer na ordem, não juntas. <c>tdvm</c> acha
/// <c>TaskDevelopmentViewModel.cs</c>. O que casa só no nome do arquivo ganha
/// do que precisa da pasta; começo de palavra e letras seguidas somam.
/// </remarks>
public static class PathReference
{
    /// <summary>Os do alias, mais as barras: o caminho dentro do ambiente faz parte do token.</summary>
    public static bool IsTokenChar(char c) => AliasCompletion.IsAliasChar(c) || c is '/' or '\\';

    public static AliasToken? FindToken(string? text, int caretIndex) =>
        AliasCompletion.FindToken(text, caretIndex, IsTokenChar);

    /// <summary>A barra invertida é aceita ao digitar, e vira <c>/</c> aqui.</summary>
    public static ReferenceQuery Parse(string query)
    {
        var normalized = query.Replace('\\', '/');
        var slash = normalized.IndexOf('/', StringComparison.Ordinal);

        return slash < 0
            ? new ReferenceQuery(null, normalized)
            : new ReferenceQuery(normalized[..slash], normalized[(slash + 1)..]);
    }

    /// <summary>
    /// A chave do ambiente: o nome da pasta do repositório, só com os
    /// caracteres que o token aceita. <c>C:\Projetos\My App</c> vira <c>My-App</c>.
    /// </summary>
    public static string KeyFor(string repositoryPath)
    {
        var name = Path.GetFileName(repositoryPath.TrimEnd('\\', '/'));
        var chars = name.Select(c => AliasCompletion.IsAliasChar(c) ? c : '-').ToArray();
        var key = new string(chars).Trim('-', '.');

        return key.Length == 0 ? "repositorio" : key;
    }

    /// <summary>
    /// Uma chave por ambiente, na ordem recebida. Dois repositórios com a mesma
    /// pasta em lugares diferentes viram <c>api</c> e <c>api-2</c>.
    /// </summary>
    public static IReadOnlyList<string> KeysFor(IEnumerable<string> repositoryPaths)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new List<string>();

        foreach (var path in repositoryPaths)
        {
            var key = KeyFor(path);
            var candidate = key;

            for (var suffix = 2; !used.Add(candidate); suffix++)
            {
                candidate = $"{key}-{suffix}";
            }

            keys.Add(candidate);
        }

        return keys;
    }

    /// <summary>
    /// A nota de <paramref name="query"/> em <paramref name="text"/> inteiro,
    /// tratado como um nome — para a chave e a branch do ambiente.
    /// </summary>
    public static int? Score(string text, string query) => Score(text, 0, 0, query);

    /// <summary>
    /// A nota de <paramref name="query"/> em <paramref name="path"/> a partir de
    /// <paramref name="from"/>, ou <c>null</c> se as letras não aparecem na
    /// ordem. Casar dentro do nome (de <paramref name="nameStart"/> em diante)
    /// vale mais; começar o nome com o digitado, mais ainda.
    /// </summary>
    public static int? Score(string path, int from, int nameStart, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        nameStart = Math.Max(nameStart, from);

        if (Subsequence(path, nameStart, query) is { } inName)
        {
            var name = path.AsSpan(nameStart);
            var bonus = 100;

            if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                bonus += 50;
            }

            var dot = name.LastIndexOf('.');
            var stem = dot > 0 ? name[..dot] : name;

            if (stem.Equals(query, StringComparison.OrdinalIgnoreCase)
                || name.Equals(query, StringComparison.OrdinalIgnoreCase))
            {
                bonus += 50;
            }

            return inName + bonus;
        }

        return Subsequence(path, from, query);
    }

    /// <summary>
    /// A melhor forma de as letras aparecerem na ordem, e não a primeira: em
    /// <c>TaskDevelopmentViewModel</c>, <c>tdvm</c> precisa pegar o <c>V</c> de
    /// "ViewModel", e não o <c>v</c> de "Development" que vem antes. Começo de
    /// palavra (depois de <c>/ . - _</c>, ou a maiúscula do camelCase) e letras
    /// seguidas somam; cada salto desconta.
    /// </summary>
    /// <remarks>
    /// Programação dinâmica em duas linhas, O(texto × busca). Roda para cada
    /// arquivo a cada tecla, por isso as linhas ficam na pilha e a conferência
    /// barata (as letras existem na ordem?) vem antes.
    /// </remarks>
    private static int? Subsequence(string text, int from, string query)
    {
        var length = text.Length - from;

        if (query.Length > length || !IsSubsequence(text, from, query))
        {
            return null;
        }

        if (length > MaxAlignedLength)
        {
            return Greedy(text, from, query);
        }

        Span<int> previous = stackalloc int[length];
        Span<int> current = stackalloc int[length];

        for (var j = 0; j < length; j++)
        {
            previous[j] = Same(text[from + j], query[0]) ? CharScore(text, from + j, from) : NoMatch;
        }

        for (var i = 1; i < query.Length; i++)
        {
            var bestBefore = NoMatch;

            for (var j = 0; j < length; j++)
            {
                if (j >= 2)
                {
                    bestBefore = Math.Max(bestBefore, previous[j - 2]);
                }

                if (!Same(text[from + j], query[i]))
                {
                    current[j] = NoMatch;
                    continue;
                }

                var run = j >= 1 && previous[j - 1] > NoMatch ? previous[j - 1] + RunBonus : NoMatch;
                var jump = bestBefore > NoMatch ? bestBefore - JumpPenalty : NoMatch;
                var best = Math.Max(run, jump);

                current[j] = best > NoMatch ? best + CharScore(text, from + j, from) : NoMatch;
            }

            var swap = previous;
            previous = current;
            current = swap;
        }

        var result = NoMatch;

        foreach (var score in previous)
        {
            result = Math.Max(result, score);
        }

        return result > NoMatch ? result : null;
    }

    private const int NoMatch = int.MinValue / 2;

    private const int RunBonus = 6;

    private const int JumpPenalty = 2;

    private const int WordStartBonus = 8;

    /// <summary>Acima disto (um caminho absurdo), a primeira ocorrência de cada letra basta.</summary>
    private const int MaxAlignedLength = 512;

    private static bool IsSubsequence(string text, int from, string query)
    {
        var matched = 0;

        for (var index = from; index < text.Length && matched < query.Length; index++)
        {
            if (Same(text[index], query[matched]))
            {
                matched++;
            }
        }

        return matched == query.Length;
    }

    private static int Greedy(string text, int from, string query)
    {
        var score = 0;
        var matched = 0;
        var last = -1;

        for (var index = from; index < text.Length && matched < query.Length; index++)
        {
            if (!Same(text[index], query[matched]))
            {
                continue;
            }

            score += CharScore(text, index, from) + (last < 0 ? 0 : index == last + 1 ? RunBonus : -JumpPenalty);
            last = index;
            matched++;
        }

        return score;
    }

    private static int CharScore(string text, int index, int from) =>
        IsWordStart(text, index, from) ? 1 + WordStartBonus : 1;

    private static bool Same(char textChar, char queryChar) =>
        char.ToUpperInvariant(textChar) == char.ToUpperInvariant(Normalize(queryChar));

    private static char Normalize(char c) => c == '\\' ? '/' : c;

    private static bool IsWordStart(string text, int index, int from)
    {
        if (index == from)
        {
            return true;
        }

        var previous = text[index - 1];
        var current = text[index];

        return previous is '/' or '.' or '-' or '_' or ' '
            || (char.IsUpper(current) && char.IsLower(previous))
            || (char.IsDigit(current) && !char.IsDigit(previous));
    }
}

/// <summary>
/// Os arquivos de um ambiente e as pastas que eles formam, prontos para a
/// busca a cada tecla. Montado uma vez por carga da lista.
/// </summary>
public sealed class PathIndex
{
    private readonly HashSet<string> _directories;

    private PathIndex(IReadOnlyList<PathEntry> entries, HashSet<string> directories, bool isTruncated)
    {
        Entries = entries;
        _directories = directories;
        IsTruncated = isTruncated;
    }

    public static PathIndex Empty { get; } = Build([]);

    /// <summary>Pastas primeiro, depois arquivos.</summary>
    public IReadOnlyList<PathEntry> Entries { get; }

    /// <summary>O repositório passou do teto da lista, e o que ficou de fora não é achado.</summary>
    public bool IsTruncated { get; }

    /// <summary>As pastas saem dos arquivos: o Git não lista pasta, e pasta vazia não interessa.</summary>
    public static PathIndex Build(IEnumerable<string> files, bool isTruncated = false)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileEntries = new List<PathEntry>();

        foreach (var raw in files)
        {
            var path = raw.Replace('\\', '/').Trim('/');

            if (path.Length == 0)
            {
                continue;
            }

            fileEntries.Add(Entry(path, isDirectory: false));

            for (var slash = path.IndexOf('/', StringComparison.Ordinal);
                 slash > 0;
                 slash = path.IndexOf('/', slash + 1))
            {
                directories.Add(path[..slash]);
            }
        }

        var entries = directories
            .Select(directory => Entry(directory, isDirectory: true))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Concat(fileEntries)
            .ToList();

        return new PathIndex(entries, directories, isTruncated);
    }

    public bool IsDirectory(string path) => _directories.Contains(path.Trim('/'));

    /// <summary>
    /// O que <paramref name="rest"/> pede. Terminar numa pasta que existe
    /// (<c>src/</c>) lista o que está nela; o que vem depois da última barra
    /// procura dentro dessa pasta. Sem pasta conhecida, a busca é no caminho
    /// inteiro — <c>views/tod</c> também acha <c>src/…/Views/TodayView.axaml</c>.
    /// </summary>
    public IReadOnlyList<PathMatch> Search(string rest, int limit)
    {
        rest = rest.Replace('\\', '/').TrimStart('/');

        var slash = rest.LastIndexOf('/');
        var directory = slash < 0 ? string.Empty : rest[..slash];
        var name = rest[(slash + 1)..];
        var scoped = directory.Length == 0 || IsDirectory(directory);

        if (scoped && name.Length == 0)
        {
            return Children(directory, limit);
        }

        var prefix = scoped && directory.Length > 0 ? directory + "/" : string.Empty;
        var query = scoped ? name : rest;
        var matches = new List<PathMatch>();

        foreach (var entry in Entries)
        {
            if (entry.Path.Length <= prefix.Length
                || !entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (PathReference.Score(entry.Path, prefix.Length, entry.NameStart, query) is { } score)
            {
                matches.Add(new PathMatch(entry, score));
            }
        }

        return Rank(matches, limit);
    }

    /// <summary>A melhor nota primeiro; no empate, o caminho mais curto — é o mais provável.</summary>
    public static IReadOnlyList<PathMatch> Rank(IEnumerable<PathMatch> matches, int limit) =>
    [
        .. matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Entry.Path.Length)
            .ThenBy(match => match.Entry.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit),
    ];

    /// <summary>O conteúdo direto da pasta, como num explorador: pastas, depois arquivos, por nome.</summary>
    private IReadOnlyList<PathMatch> Children(string directory, int limit)
    {
        directory = directory.Trim('/');

        return
        [
            .. Entries
                .Where(entry => string.Equals(entry.Parent, directory, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.IsDirectory ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(entry => new PathMatch(entry, 0)),
        ];
    }

    private static PathEntry Entry(string path, bool isDirectory) =>
        new(path, isDirectory, path.LastIndexOf('/') + 1);
}
