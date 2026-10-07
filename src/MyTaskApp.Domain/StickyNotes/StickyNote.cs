namespace MyTaskApp.Domain.StickyNotes;

/// <summary>
/// Um post-it: o que ainda não merece virar tarefa (ADR-054). Agregado próprio,
/// e não um <c>TaskItem</c> sem data — não tem ocorrência, prioridade, prazo nem
/// lembrete, e não aparece em nenhuma seção do quadro de hoje. A única ponte com
/// as tarefas é "transformar em tarefa".
/// </summary>
/// <remarks>
/// <para>
/// Três grupos de estado, separados de propósito: o <b>conteúdo</b> (texto,
/// etiqueta, cor, destaque, opacidade), a <b>janela</b> (aberta, fixada,
/// posição e tamanho) e o <b>ciclo de vida</b> (arquivado, lixeira), que segue o
/// ADR-020 — duas marcas de tempo independentes e o estado derivado delas.
/// </para>
/// <para>
/// A geometria mora aqui, e não no <c>widget.json</c> do ADR-017: são N janelas
/// que nascem e morrem com a linha, e um arquivo à parte teria de ser mantido em
/// sincronia com o banco a cada criação e expurgo.
/// </para>
/// </remarks>
public sealed class StickyNote
{
    /// <summary>Teto contra o Ctrl+V do documento errado, não contra anotação longa.</summary>
    public const int MaxContentLength = 20_000;

    public const double DefaultWidth = 280;

    public const double DefaultHeight = 200;

    /// <summary>Abaixo disto o cabeçalho não cabe e o texto vira uma fresta.</summary>
    public const double MinWidth = 200;

    public const double MinHeight = 120;

    /// <summary>Teto de sanidade: nenhuma tela razoável passa disto em DIP.</summary>
    public const double MaxSize = 4000;

    /// <summary>O mesmo piso do HUD (ADR-048): abaixo disto o fundo some atrás do texto.</summary>
    public const int MinOpacity = 70;

    public const int MaxOpacity = 100;

    private StickyNote(Guid id, DateTimeOffset createdAt)
    {
        Id = id;
        Content = string.Empty;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Width = DefaultWidth;
        Height = DefaultHeight;
        Opacity = MaxOpacity;
    }

    public Guid Id { get; }

    // --- Conteúdo -------------------------------------------------------------

    /// <summary>Texto livre. Vazio é válido: o post-it nasce assim.</summary>
    public string Content { get; private set; }

    /// <summary>
    /// A etiqueta associada. Uma só: o post-it é captura rápida, e N:N pediria
    /// o seletor inteiro do checklist. Nula também quando a etiqueta foi excluída
    /// (o banco aplica <c>SET NULL</c>).
    /// </summary>
    public Guid? TagId { get; private set; }

    public StickyNoteColorMode ColorMode { get; private set; }

    /// <summary>Preenchida só com <see cref="StickyNoteColorMode.Palette"/>.</summary>
    public StickyNotePaletteColor? PaletteColor { get; private set; }

    public StickyNoteEmphasis Emphasis { get; private set; }

    /// <summary>Opacidade do fundo, em %. O texto nunca fica translúcido.</summary>
    public int Opacity { get; private set; }

    // --- Janela ----------------------------------------------------------------

    /// <summary>
    /// A janela estava aberta. Junto com <see cref="IsPinned"/>, é o que decide
    /// quem reabre sozinho quando o app sobe.
    /// </summary>
    public bool IsOpen { get; private set; }

    /// <summary>Sempre no topo. Independente de qualquer escolha da janela principal.</summary>
    public bool IsPinned { get; private set; }

    /// <summary>Canto superior esquerdo em pixels físicos; nulo até a primeira colocação.</summary>
    public int? X { get; private set; }

    public int? Y { get; private set; }

    /// <summary>Largura em DIP — independente da escala do monitor.</summary>
    public double Width { get; private set; }

    public double Height { get; private set; }

    // --- Ciclo de vida -------------------------------------------------------

    public DateTimeOffset CreatedAt { get; }

    /// <summary>A última mudança de texto. Posição e cor não contam: não são "mexi na anotação".</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>
    /// A tarefa que este post-it virou. Sem chave estrangeira, como a trilha do
    /// ADR-020: a tarefa pode ser expurgada depois, e a referência continua
    /// dizendo o que aconteceu.
    /// </summary>
    public Guid? ConvertedTaskId { get; private set; }

    public StickyNoteLifecycle Lifecycle =>
        DeletedAt is not null ? StickyNoteLifecycle.Trashed
        : ArchivedAt is not null ? StickyNoteLifecycle.Archived
        : StickyNoteLifecycle.Active;

    public bool IsArchived => ArchivedAt is not null;

    public bool IsInTrash => DeletedAt is not null;

    public bool IsOutOfTheMainList => IsArchived || IsInTrash;

    public bool IsBlank => string.IsNullOrWhiteSpace(Content);

    /// <summary>
    /// A cor que vale de fato. "Cor da etiqueta" sem etiqueta — porque ela foi
    /// excluída — volta para o tema, em vez de desenhar uma cor que não existe.
    /// </summary>
    public StickyNoteColorMode EffectiveColorMode =>
        ColorMode == StickyNoteColorMode.Tag && TagId is null ? StickyNoteColorMode.Theme : ColorMode;

    public static StickyNote Create(DateTimeOffset createdAt) =>
        new(Guid.CreateVersion7(createdAt), createdAt) { IsOpen = true };

    public void Edit(string? content, DateTimeOffset at)
    {
        RefuseWhenOutOfTheMainList("editar");

        var normalized = NormalizeContent(content);

        if (normalized == Content)
        {
            return;
        }

        Content = normalized;
        UpdatedAt = at;
    }

    /// <summary>
    /// Atômico: o estado final inteiro, validado antes de qualquer atribuição —
    /// a mesma forma do <c>SetTaskTags</c>, que torna repetir a chamada inócuo.
    /// </summary>
    public void ChangeAppearance(
        Guid? tagId,
        StickyNoteColorMode colorMode,
        StickyNotePaletteColor? paletteColor,
        StickyNoteEmphasis emphasis,
        int opacity)
    {
        RefuseWhenOutOfTheMainList("alterar");

        if (!Enum.IsDefined(colorMode) || !Enum.IsDefined(emphasis))
        {
            throw new DomainException("Aparência de post-it desconhecida.");
        }

        if (colorMode == StickyNoteColorMode.Palette
            && (paletteColor is not { } chosen || !Enum.IsDefined(chosen)))
        {
            throw new DomainException("Escolha uma cor para o post-it.");
        }

        if (colorMode == StickyNoteColorMode.Tag && tagId is null)
        {
            throw new DomainException("Escolha uma etiqueta para usar a cor dela.");
        }

        if (opacity is < MinOpacity or > MaxOpacity)
        {
            throw new DomainException(
                $"A opacidade do post-it fica entre {MinOpacity}% e {MaxOpacity}%.");
        }

        TagId = tagId;
        ColorMode = colorMode;
        PaletteColor = colorMode == StickyNoteColorMode.Palette ? paletteColor : null;
        Emphasis = emphasis;
        Opacity = opacity;
    }

    public void Open()
    {
        RefuseWhenOutOfTheMainList("abrir");

        IsOpen = true;
    }

    /// <summary>Fechar é esconder, nunca excluir: vale em qualquer estado.</summary>
    public void Close() => IsOpen = false;

    public void Pin(bool pinned)
    {
        RefuseWhenOutOfTheMainList("fixar");

        IsPinned = pinned;
    }

    /// <summary>
    /// Grava onde a janela está. Medidas fora do razoável são trazidas para
    /// dentro em vez de recusadas: quem chama é o arrasto, não o usuário digitando.
    /// </summary>
    public void Place(int? x, int? y, double width, double height)
    {
        X = x;
        Y = y;
        Width = Sanitize(width, MinWidth, DefaultWidth);
        Height = Sanitize(height, MinHeight, DefaultHeight);
    }

    /// <summary>Tira da lista principal e fecha a janela; o conteúdo fica intacto.</summary>
    public void Archive(DateTimeOffset at)
    {
        RefuseWhenInTrash("arquivar");

        if (IsArchived)
        {
            throw new DomainException("Este post-it já está arquivado.");
        }

        ArchivedAt = at;
        IsOpen = false;
    }

    public void RestoreFromArchive()
    {
        RefuseWhenInTrash("restaurar");

        if (!IsArchived)
        {
            throw new DomainException("Este post-it não está arquivado.");
        }

        ArchivedAt = null;
    }

    /// <summary>Exclusão reversível: volta de onde veio até o fim do prazo da lixeira.</summary>
    public void MoveToTrash(DateTimeOffset at)
    {
        if (IsInTrash)
        {
            throw new DomainException("Este post-it já está na lixeira.");
        }

        DeletedAt = at;
        IsOpen = false;
    }

    /// <summary>Arquivado antes de ir para a lixeira continua arquivado (ADR-020).</summary>
    public void RestoreFromTrash()
    {
        if (!IsInTrash)
        {
            throw new DomainException("Este post-it não está na lixeira.");
        }

        DeletedAt = null;
    }

    /// <summary>
    /// Só o que já saiu da lista principal é apagado de vez — com uma exceção
    /// que é a razão de o post-it existir: um post-it em branco não guarda nada,
    /// e "Novo Post-it" seguido de X não pode deixar uma linha vazia na lista.
    /// </summary>
    public void EnsurePermanentDeletionIsAllowed()
    {
        if (!IsOutOfTheMainList && !IsBlank)
        {
            throw new DomainException(
                "Só é possível excluir definitivamente um post-it que esteja "
                + "arquivado ou na lixeira.");
        }
    }

    /// <summary>
    /// Só o que está na lista principal vira tarefa — inteiro ou um trecho. O
    /// guardado se restaura primeiro, como em qualquer outra alteração.
    /// </summary>
    public void EnsureCanBeConverted() => RefuseWhenOutOfTheMainList("transformar em tarefa");

    /// <summary>Virou tarefa: guarda a referência e arquiva, para continuar recuperável.</summary>
    public void MarkConverted(Guid taskId, DateTimeOffset at)
    {
        EnsureCanBeConverted();
        Archive(at);

        ConvertedTaskId = taskId;
    }

    private static string NormalizeContent(string? content)
    {
        var normalized = content ?? string.Empty;

        if (normalized.Length > MaxContentLength)
        {
            throw new DomainException(
                $"O post-it não pode passar de {MaxContentLength:N0} caracteres.");
        }

        // Só espaços é o mesmo que nada: evita um post-it "cheio" de nada.
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized;
    }

    private static double Sanitize(double value, double min, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, MaxSize) : fallback;

    private void RefuseWhenInTrash(string verb)
    {
        if (IsInTrash)
        {
            throw new DomainException($"Não é possível {verb} um post-it que está na lixeira.");
        }
    }

    /// <summary>Arquivado e na lixeira são somente leitura, como o checklist (ADR-020).</summary>
    private void RefuseWhenOutOfTheMainList(string verb)
    {
        RefuseWhenInTrash(verb);

        if (IsArchived)
        {
            throw new DomainException(
                $"Não é possível {verb} um post-it arquivado. Restaure-o primeiro.");
        }
    }
}
