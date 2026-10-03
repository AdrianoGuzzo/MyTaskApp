namespace MyTaskApp.Infrastructure.Persistence;

/// <summary>
/// As convenções de nome de branch (ADR-045), em linha única. Guardadas como o
/// texto que o usuário escreveu, uma linha por tipo: é o que a tela edita, e
/// uma coluna por tipo não caberia em tipos que cada time inventa.
/// </summary>
internal sealed class BranchSettingsRow
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public string Conventions { get; set; } = string.Empty;
}
