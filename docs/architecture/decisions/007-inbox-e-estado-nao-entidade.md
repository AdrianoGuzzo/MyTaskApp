# ADR-007 — Inbox é estado, não entidade

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Parcial |
| **Categoria** | dominio |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | Inbox, TriagedAt, triagem, InboxItem, tarefa sem data |
| **Relações** | relacionado: [ADR-013](013-captura-rapida-uma-linha-uma-tarefa.md), [ADR-054](054-post-it-entidade-propria.md) |
| **Código** | `src/MyTaskApp.Domain/Tasks/TaskItem.cs` |

**Decisão:** Inbox = `TaskItem` ainda não triado (`TriagedAt == null`).

**Por quê:** uma entidade `InboxItem` separada exigiria conversão, duplicação de
campos e um segundo modelo. O fluxo do §36 ("pensei → registrei → organizei
quando necessário") é exatamente uma tarefa que ainda não recebeu classificação.

## Divergências com o código (conferido em 2026-10-24)

- TriagedAt não existe no TaskItem nem em lugar nenhum de src/ (src/MyTaskApp.Domain/Tasks/TaskItem.cs). Na prática o papel de "Inbox" é a tarefa sem data (ScheduledDate nulo, seção Unscheduled), como mostra tests/MyTaskApp.Application.Tests/Tasks/CreateTaskHandlerTests.cs.
