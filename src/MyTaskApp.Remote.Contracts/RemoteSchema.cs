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

/// <summary>As rotas da orquestração no servidor, ao lado das do sync (<c>/api/v1</c>).</summary>
public static class RemoteRoutes
{
    public const string Prefix = "/api/v1/remote";

    /// <summary><c>GET</c>: os dispositivos da conta.</summary>
    public const string Devices = Prefix + "/devices";

    /// <summary><c>PUT</c>: o dispositivo do token registra a chave, os papéis e as capacidades.</summary>
    public const string CurrentDevice = Devices + "/current";
}
