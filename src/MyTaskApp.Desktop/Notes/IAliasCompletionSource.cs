namespace MyTaskApp.Desktop.Notes;

/// <summary>
/// Uma lista de <c>@alias</c> que o <see cref="AliasCompletionBinder"/> sabe
/// ligar a uma caixa de texto: os diretórios das etiquetas (ADR-026) e os
/// comandos globais (ADR-028). O binder cuida do controle; a fonte, do que a
/// lista mostra e do que aceitar insere.
/// </summary>
public interface IAliasCompletionSource
{
    /// <summary>Desligada (tarefa só de leitura, execução em andamento), a lista nunca abre.</summary>
    bool IsEnabled { get; }

    bool IsCompletionOpen { get; }

    /// <summary>O <c>@texto</c> que a lista aberta completa.</summary>
    AliasToken? CompletionToken { get; }

    /// <summary>O item destacado pelo teclado.</summary>
    object? SelectedItem { get; }

    void UpdateCompletion(string? text, int caretIndex);

    void MoveSelection(int delta);

    void DismissCompletion();

    /// <summary>
    /// O texto que aceitar <paramref name="suggestion"/> põe no lugar do
    /// <c>@texto</c>, ou <c>null</c> se ela não é desta lista.
    /// </summary>
    string? ReplacementFor(object? suggestion);

    /// <summary>
    /// O que o Tab põe no lugar do <c>@texto</c> para a busca <b>continuar</b>
    /// — entrar numa pasta (ADR-039). <c>null</c> = o Tab aceita, como o Enter.
    /// </summary>
    string? ContinuationFor(object? suggestion) => null;

    /// <summary>Os caracteres do <c>@texto</c>: o que aceitar troca, até o fim dele.</summary>
    bool IsTokenChar(char c) => AliasCompletion.IsAliasChar(c);
}
