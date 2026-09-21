namespace MyTaskApp.Domain.Lifecycle;

/// <summary>
/// Onde o checklist está no seu ciclo de vida. É o único lugar que responde
/// isso: as telas, as consultas e as varreduras automáticas perguntam a
/// <see cref="Tasks.TaskItem.Lifecycle"/> em vez de reimplementar
/// "arquivado = ArchivedAt != null" cada uma do seu jeito (§9).
/// </summary>
/// <remarks>
/// <para>
/// A ordem dos valores é a ordem de precedência com que o estado é derivado:
/// na lixeira vence arquivado, que vence concluído. É isso que faz restaurar da
/// lixeira devolver o checklist ao estado em que ele estava — arquivado continua
/// arquivado — sem precisar de um campo "estado anterior".
/// </para>
/// <para>
/// <b>Não existe aqui um quinto valor para "excluído definitivamente".</b> Esse
/// é o estado terminal do §9, e ele não é um estado da entidade: é a ausência
/// dela. Um valor de enum que nenhuma linha do banco pode carregar seria código
/// morto com aparência de regra. O que sobrevive à exclusão definitiva é a
/// trilha de auditoria, e lá ele tem nome:
/// <see cref="Auditing.TaskAuditOperation.PermanentlyDeleted"/>.
/// </para>
/// </remarks>
public enum TaskLifecycle
{
    /// <summary>Tem pelo menos uma ocorrência pendente.</summary>
    Active = 0,

    /// <summary>Nenhuma ocorrência pendente e pelo menos uma concluída.</summary>
    Completed = 1,

    /// <summary>Fora da lista principal, com todos os dados preservados.</summary>
    Archived = 2,

    /// <summary>Na lixeira, restaurável até o fim do prazo de retenção.</summary>
    Trashed = 3,
}
