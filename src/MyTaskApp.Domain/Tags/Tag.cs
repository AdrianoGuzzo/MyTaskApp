namespace MyTaskApp.Domain.Tags;

/// <summary>
/// Uma etiqueta que o usuário cria e reaproveita em quantos checklists quiser.
/// Nome e cor moram só aqui: o checklist guarda o vínculo, nunca uma cópia, e
/// por isso renomear ou recolorir aparece em todo lugar de uma vez (ADR-025).
/// </summary>
public sealed class Tag
{
    public const int MaxNameLength = 40;

    private readonly List<TagDirectory> _directories = [];

    private Tag(Guid id, string name, string colorHex, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        ColorHex = colorHex;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public string Name { get; private set; }

    /// <summary>Sempre <c>#RRGGBB</c> (ver <see cref="TagColor.Normalize"/>).</summary>
    public string ColorHex { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>As pastas da etiqueta, com o alias que as chama na anotação (ADR-026).</summary>
    public IReadOnlyList<TagDirectory> Directories => _directories.AsReadOnly();

    public static Tag Create(string name, string colorHex, DateTimeOffset createdAt) =>
        new(Guid.CreateVersion7(createdAt), NormalizeName(name), TagColor.Normalize(colorHex), createdAt);

    /// <summary>Atômico: valida os dois campos antes de trocar qualquer um.</summary>
    public void Update(string name, string colorHex)
    {
        var normalizedName = NormalizeName(name);
        var normalizedColor = TagColor.Normalize(colorHex);

        Name = normalizedName;
        ColorHex = normalizedColor;
    }

    public static string NormalizeName(string? name)
    {
        var normalized = name?.Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            throw new DomainException("A etiqueta precisa de um nome.");
        }

        if (normalized.Length > MaxNameLength)
        {
            throw new DomainException(
                $"O nome da etiqueta não pode passar de {MaxNameLength} caracteres.");
        }

        return normalized;
    }

    /// <summary>
    /// Acrescenta uma pasta. O alias é único <b>dentro da etiqueta</b>: duas
    /// etiquetas podem ter um <c>@api</c> cada, e o autocomplete mostra de qual
    /// etiqueta é cada um.
    /// </summary>
    public TagDirectory AddDirectory(
        string alias,
        string path,
        string? name,
        string? description,
        DateTimeOffset createdAt,
        string? defaultBranch = null)
    {
        var directory = TagDirectory.Create(Id, alias, path, name, description, defaultBranch, createdAt);

        EnsureAliasIsFree(directory.Alias, exceptId: null);

        _directories.Add(directory);

        return directory;
    }

    public void UpdateDirectory(
        Guid directoryId,
        string alias,
        string path,
        string? name,
        string? description,
        string? defaultBranch = null)
    {
        var directory = GetDirectory(directoryId);

        EnsureAliasIsFree(TagDirectory.NormalizeAlias(alias), directoryId);

        directory.Update(alias, path, name, description, defaultBranch);
    }

    public void RemoveDirectory(Guid directoryId) => _directories.Remove(GetDirectory(directoryId));

    private TagDirectory GetDirectory(Guid directoryId) =>
        _directories.Find(directory => directory.Id == directoryId)
        ?? throw new DomainException("Diretório não encontrado.");

    private void EnsureAliasIsFree(string alias, Guid? exceptId)
    {
        if (_directories.Exists(directory =>
                directory.Id != exceptId
                && string.Equals(directory.Alias, alias, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainException($"Esta etiqueta já tem um diretório {alias}.");
        }
    }
}
