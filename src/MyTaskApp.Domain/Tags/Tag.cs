namespace MyTaskApp.Domain.Tags;

/// <summary>
/// Uma etiqueta que o usuário cria e reaproveita em quantos checklists quiser.
/// Nome e cor moram só aqui: o checklist guarda o vínculo, nunca uma cópia, e
/// por isso renomear ou recolorir aparece em todo lugar de uma vez (ADR-025).
/// </summary>
public sealed class Tag
{
    public const int MaxNameLength = 40;

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
}
