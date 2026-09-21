namespace MyTaskApp.Domain.Auditing;

/// <summary>
/// Quem originou a operação. Separar isto do nome do usuário é o que torna
/// "arquivado por engano" distinguível de "arquivado pela varredura" quando
/// alguém for investigar meses depois (§10).
/// </summary>
public enum AuditActor
{
    User = 0,
    System = 1,
}
