namespace MyTaskApp.Desktop.Composition;

/// <summary>
/// Iniciar com o Windows (ADR-023). A verdade mora na chave <c>Run</c> do
/// registro — este é o único jeito de o app chegar nela.
/// </summary>
/// <remarks>
/// Interface, ao contrário do que o ADR-018 prescreve para o resto do
/// packaging, porque aqui há mesmo duas implementações: a do Windows e a que
/// responde "não dá" no Linux e nos testes headless. Escrever no registro de
/// verdade durante <c>dotnet test</c> não é opção.
/// </remarks>
public interface IStartupRegistration
{
    /// <summary>
    /// <c>false</c> fora do Windows. Quem mostra a opção esconde o item em vez
    /// de exibir uma caixa que nunca vai marcar.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>Se o app sobe sozinho no próximo login.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// <c>true</c> se gravou. Quem chama relê <see cref="IsEnabled"/> de
    /// qualquer forma: o visto precisa contar a verdade, não o clique.
    /// </summary>
    bool Enable();

    bool Disable();
}

/// <summary>
/// O objeto nulo: nenhuma plataforma, nenhuma escrita, nenhum erro. É o que o
/// Linux recebe, e o que a moldura do painel usa até o composition root ligá-la
/// ao registro de verdade.
/// </summary>
public sealed class UnsupportedStartupRegistration : IStartupRegistration
{
    public static UnsupportedStartupRegistration Instance { get; } = new();

    public bool IsSupported => false;

    public bool IsEnabled => false;

    public bool Enable() => false;

    public bool Disable() => false;
}
