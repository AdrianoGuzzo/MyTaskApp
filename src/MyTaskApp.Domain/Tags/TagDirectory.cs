namespace MyTaskApp.Domain.Tags;

/// <summary>
/// Uma pasta ligada a uma etiqueta: o path que o usuário digitaria à mão, com um
/// <c>@alias</c> curto para chamá-lo na anotação (ADR-026).
/// </summary>
/// <remarks>
/// O alias é só atalho de digitação. Escolhê-lo insere o <see cref="Path"/> no
/// texto, e o texto não guarda nenhuma referência a este registro — mudar o
/// path depois não reescreve anotação nenhuma, de propósito. O diretório é
/// entidade, com identidade própria, porque a integração com Git vai se prender
/// a ele.
/// </remarks>
public sealed class TagDirectory
{
    public const int MaxAliasLength = AliasRule.MaxLength;

    public const int MaxPathLength = 1024;

    public const int MaxNameLength = 80;

    public const int MaxDescriptionLength = 500;

    private TagDirectory(
        Guid id,
        Guid tagId,
        string alias,
        string path,
        string? name,
        string? description,
        DateTimeOffset createdAt)
    {
        Id = id;
        TagId = tagId;
        Alias = alias;
        Path = path;
        Name = name;
        Description = description;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid TagId { get; }

    /// <summary>Sempre começa com <c>@</c> (ver <see cref="NormalizeAlias"/>).</summary>
    public string Alias { get; private set; }

    /// <summary>Caminho absoluto. Pode não existir: a pasta é conferida só na exibição.</summary>
    public string Path { get; private set; }

    public string? Name { get; private set; }

    public string? Description { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    internal static TagDirectory Create(
        Guid tagId,
        string alias,
        string path,
        string? name,
        string? description,
        DateTimeOffset createdAt) =>
        new(
            Guid.CreateVersion7(createdAt),
            tagId,
            NormalizeAlias(alias),
            NormalizePath(path),
            NormalizeOptional(name, MaxNameLength, "O nome do diretório"),
            NormalizeOptional(description, MaxDescriptionLength, "A descrição do diretório"),
            createdAt);

    /// <summary>Atômico: valida tudo antes de trocar qualquer campo.</summary>
    internal void Update(string alias, string path, string? name, string? description)
    {
        var normalizedAlias = NormalizeAlias(alias);
        var normalizedPath = NormalizePath(path);
        var normalizedName = NormalizeOptional(name, MaxNameLength, "O nome do diretório");
        var normalizedDescription = NormalizeOptional(
            description, MaxDescriptionLength, "A descrição do diretório");

        Alias = normalizedAlias;
        Path = normalizedPath;
        Name = normalizedName;
        Description = normalizedDescription;
    }

    /// <summary>
    /// Apara e põe o <c>@</c> quando falta (ver <see cref="AliasRule.Normalize"/>).
    /// </summary>
    public static string NormalizeAlias(string? alias) =>
        AliasRule.Normalize(alias, "O diretório precisa de um alias (ex.: @meu-projeto).");

    /// <summary>
    /// Apara, tira as aspas que o "Copiar como caminho" do Explorer põe e a
    /// barra final. Exige caminho absoluto; a pasta pode ainda não existir.
    /// </summary>
    public static string NormalizePath(string? path)
    {
        var normalized = path?.Trim().Trim('"').Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            throw new DomainException("O diretório precisa de um caminho.");
        }

        if (normalized.Length > MaxPathLength)
        {
            throw new DomainException(
                $"O caminho não pode passar de {MaxPathLength} caracteres.");
        }

        if (!System.IO.Path.IsPathFullyQualified(normalized))
        {
            throw new DomainException(
                "Informe o caminho completo da pasta (ex.: C:\\Projetos\\meu-projeto).");
        }

        // A raiz fica como está: "C:\" sem a barra vira "C:", que é outra coisa.
        var root = System.IO.Path.GetPathRoot(normalized) ?? string.Empty;

        while (normalized.Length > root.Length
               && (normalized[^1] == '\\' || normalized[^1] == '/'))
        {
            normalized = normalized[..^1];
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maxLength, string label)
    {
        var normalized = value?.Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > maxLength)
        {
            throw new DomainException($"{label} não pode passar de {maxLength} caracteres.");
        }

        return normalized;
    }
}
