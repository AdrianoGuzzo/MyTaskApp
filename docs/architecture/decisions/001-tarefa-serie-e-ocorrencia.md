# ADR-001 — `TaskItem` (série) + `TaskOccurrence` (unidade de trabalho)

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | dominio |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | TaskItem, TaskOccurrence, ocorrência, série, modelo uniforme, histórico |
| **Relações** | relacionado: [ADR-003](003-materializacao-de-ocorrencias-por-horizonte.md) |
| **Código** | `src/MyTaskApp.Domain/Tasks/TaskItem.cs`, `src/MyTaskApp.Domain/Tasks/TaskOccurrence.cs` |

**Decisão:** modelo uniforme. *Toda* tarefa tem pelo menos uma ocorrência —
inclusive as não recorrentes.

```
TaskItem          → o QUE é a tarefa (título, descrição, prioridade, categoria,
                    projeto, regra de recorrência, lembretes)
TaskOccurrence    → o QUANDO + o ESTADO (data/hora agendada, status, concluída em)
```

**Alternativa rejeitada:** tarefa simples guarda status/data nela mesma, e só
tarefas recorrentes geram ocorrências.

**Por quê:** a alternativa cria um `if (isRecurring)` que vaza para *todas* as
consultas ("Hoje", histórico, atrasadas), para a conclusão e para os lembretes.
O briefing pede explicitamente (§11) que a recorrência não se espalhe pelo
código. O modelo uniforme paga um custo pequeno — uma linha extra por tarefa
simples — e em troca todo o app lê e escreve **um único conceito**: ocorrência.

**Custo aceito:** 2 inserts na criação de uma tarefa simples; o agregado
`TaskItem` garante por invariante que uma tarefa não recorrente tem
exatamente 1 ocorrência.

**Consequência desejada (§34):** o histórico é preservado por natureza.
`17/09 concluída · 18/09 concluída · 19/09 pendente` são três linhas distintas,
não uma linha que muda de data.

## Divergências com o código (conferido em 2026-10-24)

- O diagrama põe categoria, projeto e regra de recorrência no TaskItem; nenhum dos três existe como campo (src/MyTaskApp.Domain/Tasks/TaskItem.cs). Categoria virou etiqueta (ADR-025) e recorrência não foi escrita.
