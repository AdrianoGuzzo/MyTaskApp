namespace MyTaskApp.Remote.Contracts;

/// <summary>Os nomes do protocolo que o servidor e os clientes precisam escrever igual.</summary>
public static class RemoteSchema
{
    /// <summary>
    /// O escopo OAuth da orquestração remota (RS-07, ADR-087). Vem junto com o
    /// <c>sync</c>, nunca no lugar dele, e só é pedido por quem liga o controle
    /// remoto: o login de quem só sincroniza continua o mesmo.
    /// </summary>
    public const string Scope = "remote";
}
