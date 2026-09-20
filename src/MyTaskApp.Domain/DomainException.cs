namespace MyTaskApp.Domain;

/// <summary>
/// Violação de uma regra de negócio. Mensagens ficam em pt-BR porque chegam ao
/// usuário como validação; a UI as exibe sem precisar traduzir.
/// </summary>
public class DomainException(string message) : Exception(message);
