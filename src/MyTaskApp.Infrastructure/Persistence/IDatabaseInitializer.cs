namespace MyTaskApp.Infrastructure.Persistence;

/// <summary>
/// Prepara o banco na inicialização do aplicativo: cria a pasta e aplica as
/// migrations pendentes (§19 — nunca EnsureCreated).
/// </summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
