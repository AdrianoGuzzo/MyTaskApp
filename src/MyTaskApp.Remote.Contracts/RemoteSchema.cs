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

    /// <summary><c>POST</c>: o controlador pede um pareamento. <c>GET</c>: os pedidos em aberto para este executor.</summary>
    public const string Pairings = Prefix + "/pairings";

    public static string Pairing(Guid pairingId) => $"{Pairings}/{pairingId:D}";

    /// <summary><c>POST</c>: o executor aprova, assinando com a chave dele.</summary>
    public static string ApprovePairing(Guid pairingId) => $"{Pairing(pairingId)}/approve";

    /// <summary><c>PUT</c>: o agente publica o catálogo desta máquina (ADR-088).</summary>
    public const string CurrentCatalog = CurrentDevice + "/catalog";

    /// <summary><c>GET</c>: o catálogo publicado por uma máquina da conta.</summary>
    public static string Catalog(Guid deviceId) => $"{Devices}/{deviceId:D}/catalog";

    /// <summary><c>POST</c> (envelope assinado): o controlador cria. <c>GET</c>: as mais recentes da conta.</summary>
    public const string Operations = Prefix + "/operations";

    public static string Operation(Guid operationId) => $"{Operations}/{operationId:D}";

    /// <summary><c>POST</c>: o agente alvo muda o estado (<see cref="OperationStatusUpdate"/>).</summary>
    public static string OperationStatus(Guid operationId) => $"{Operation(operationId)}/status";

    /// <summary><c>POST</c>: o agente manda um lote. <c>GET ?after=N</c>: quem é da conta lê.</summary>
    public static string OperationEvents(Guid operationId) => $"{Operation(operationId)}/events";

    /// <summary><c>POST</c>: qualquer dispositivo da conta pede para cancelar.</summary>
    public static string CancelOperation(Guid operationId) => $"{Operation(operationId)}/cancel";

    /// <summary><c>POST</c>: o agente alvo pede uma aprovação (<see cref="ApprovalRequest"/>).</summary>
    public static string OperationApprovals(Guid operationId) => $"{Operation(operationId)}/approvals";

    /// <summary><c>GET</c>: o que o agente da máquina do token tem a fazer.</summary>
    public const string AgentOperations = Prefix + "/agent/operations";

    /// <summary><c>GET</c>: as aprovações em aberto da conta.</summary>
    public const string Approvals = Prefix + "/approvals";

    public static string Approval(Guid approvalId) => $"{Approvals}/{approvalId:D}";

    /// <summary><c>POST</c> (envelope assinado): o controlador responde.</summary>
    public static string ApprovalResponse(Guid approvalId) => $"{Approval(approvalId)}/response";

    /// <summary>O hub SignalR (ADR-086): só campainha; o estado é todo do REST.</summary>
    public const string Hub = "/hubs/remote";
}

/// <summary>
/// O que o hub avisa (servidor → cliente). Só ids: quem recebe busca o resto
/// por REST, então um aviso perdido ou repetido não muda nada.
/// </summary>
public static class RemoteHubMessages
{
    /// <summary>Para o agente alvo: (operationId).</summary>
    public const string OperationAvailable = "OperationAvailable";

    /// <summary>Para o agente alvo: (operationId).</summary>
    public const string CancelRequested = "CancelRequested";

    /// <summary>Para o agente alvo: (approvalId, operationId).</summary>
    public const string ApprovalResolved = "ApprovalResolved";

    /// <summary>Para o executor: (pairingId).</summary>
    public const string PairingRequested = "PairingRequested";

    /// <summary>Para a conta: (operationId, status).</summary>
    public const string OperationChanged = "OperationChanged";

    /// <summary>Para a conta: (operationId, lastContiguousSeq).</summary>
    public const string EventsAppended = "EventsAppended";

    /// <summary>Para a conta: (approvalId, operationId).</summary>
    public const string ApprovalRequested = "ApprovalRequested";

    /// <summary>Para a conta: (pairingId).</summary>
    public const string PairingApproved = "PairingApproved";
}
