using MyTaskApp.Domain.Devices;

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

    /// <summary>
    /// Em qual computador a etiqueta aparece para ser escolhida (ADR-062).
    /// <c>null</c> = legado, aparece em todos. O chip numa tarefa visível aparece
    /// sempre: o vínculo segue a tarefa.
    /// </summary>
    public PlacementKind? PlacementKind { get; private set; }

    public Guid? PlacementDeviceId { get; private set; }

    public DevicePlacement? Placement => DevicePlacement.From(PlacementKind, PlacementDeviceId);

    public static Tag Create(string name, string colorHex, DateTimeOffset createdAt, DevicePlacement? placement = null)
    {
        var tag = new Tag(Guid.CreateVersion7(createdAt), NormalizeName(name), TagColor.Normalize(colorHex), createdAt);

        if (placement is not null)
        {
            tag.Place(placement);
        }

        return tag;
    }

    public void Place(DevicePlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        PlacementKind = placement.Kind;
        PlacementDeviceId = placement.DeviceId;
    }

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
        string? defaultBranch = null,
        string? repositoryUrl = null)
    {
        var directory = TagDirectory.Create(Id, alias, path, name, description, defaultBranch, createdAt, repositoryUrl);

        EnsureAliasIsFree(directory.Alias, exceptId: null);

        _directories.Add(directory);

        return directory;
    }

    public void UpdateDirectory(
        Guid directoryId,
        string alias,
        string? path,
        string? name,
        string? description,
        string? defaultBranch = null,
        string? repositoryUrl = null)
    {
        var directory = GetDirectory(directoryId);

        EnsureAliasIsFree(TagDirectory.NormalizeAlias(alias), directoryId);

        directory.Update(alias, path, name, description, defaultBranch, repositoryUrl);
    }

    /// <summary>
    /// A URL do repositório acompanhando o <c>origin</c> da pasta (ADR-064).
    /// <c>true</c> quando mudou de fato — só então vale gravar.
    /// </summary>
    public bool ChangeDirectoryRepositoryUrl(Guid directoryId, string? repositoryUrl) =>
        GetDirectory(directoryId).ChangeRepositoryUrl(repositoryUrl);

    public void RemoveDirectory(Guid directoryId) => _directories.Remove(GetDirectory(directoryId));

    /// <summary>
    /// Oferece um comando global como botão nos worktrees do diretório
    /// (ADR-051). Entra no fim da lista, ligado. Que o comando existe é o caso de
    /// uso quem confere: ele é de outro agregado.
    /// </summary>
    public TagDirectoryCommand AddDirectoryCommand(Guid directoryId, Guid developmentCommandId, DateTimeOffset at) =>
        GetDirectory(directoryId).AddCommand(developmentCommandId, at);

    /// <summary>Personaliza só neste diretório. Em branco volta à configuração global.</summary>
    public void CustomizeDirectoryCommand(
        Guid directoryId,
        Guid bindingId,
        string? commandOverride,
        string? workingDirectoryOverride) =>
        GetDirectory(directoryId).CustomizeCommand(bindingId, commandOverride, workingDirectoryOverride);

    /// <summary>Desligar esconde o botão sem perder a associação nem a personalização.</summary>
    public void SetDirectoryCommandEnabled(Guid directoryId, Guid bindingId, bool enabled) =>
        GetDirectory(directoryId).EnableCommand(bindingId, enabled);

    public void MoveDirectoryCommand(Guid directoryId, Guid bindingId, int offset) =>
        GetDirectory(directoryId).MoveCommand(bindingId, offset);

    public void RemoveDirectoryCommand(Guid directoryId, Guid bindingId) =>
        GetDirectory(directoryId).RemoveCommand(bindingId);

    /// <summary>A associação, para o caso de uso saber a que comando ela aponta.</summary>
    public TagDirectoryCommand GetDirectoryCommand(Guid directoryId, Guid bindingId) =>
        GetDirectory(directoryId).GetCommand(bindingId);

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
