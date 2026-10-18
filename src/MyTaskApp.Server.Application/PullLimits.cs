namespace MyTaskApp.Server.Application;

/// <summary>
/// Os tetos de uma página do pull (ADR-071), contando o que vem de carona. O
/// <c>limit</c> do cliente conta só o log; estes contam a resposta inteira.
/// </summary>
/// <remarks>
/// A página corta antes do item que passaria do teto, e o cliente continua
/// pelo <c>hasMore</c>. O primeiro item sempre entra, com o que ele exige,
/// para o cursor andar: só ele, sozinho, passa do teto.
/// </remarks>
public sealed class PullLimits
{
    /// <summary>Entidades por página.</summary>
    public int MaxEntities { get; set; } = 1_000;

    /// <summary>Bytes por página, somando o JSON de cada mudança.</summary>
    public int MaxBytes { get; set; } = 1_048_576;
}
