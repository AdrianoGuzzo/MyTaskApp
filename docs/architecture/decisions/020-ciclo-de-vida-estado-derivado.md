# ADR-020 — Ciclo de vida do checklist: estado derivado, não coluna de estado

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ciclo-de-vida |
| **Registrado** | 2026-09-21, commit `9323e7e` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | TaskLifecycle, ArchivedAt, DeletedAt, ConcludedAt, TaskAuditEntries, lixeira, arquivamento automático, AuditActor |
| **Relações** | relacionado: [ADR-014](014-padrao-de-lembrete-no-banco.md), [ADR-021](021-agendador-de-manutencao-do-ciclo-de-vida.md), [ADR-022](022-ordem-manual-por-arrasto.md), [ADR-024](024-anotacao-markdown-proprio.md), [ADR-025](025-etiquetas-n-n-bolinhas.md), [ADR-053](053-historico-sete-dias-projecao.md), [ADR-054](054-post-it-entidade-propria.md) |
| **Código** | `src/MyTaskApp.Domain/Tasks/TaskItem.cs`, `src/MyTaskApp.Domain/Lifecycle/DataRetentionPolicy.cs`, `src/MyTaskApp.Infrastructure/Persistence/Configurations/TaskAuditEntryConfiguration.cs`, `src/MyTaskApp.Application/Abstractions/ICurrentUser.cs` |

**Decisão:** arquivar, lixeira e exclusão definitiva entram como **três marcas
de tempo no `TaskItem`** — `ArchivedAt`, `DeletedAt` (com `DeletedBy`) e
`ConcludedAt` — e o estado do §9 é **derivado** delas em `TaskItem.Lifecycle`:

```
DeletedAt   != null → Lixeira      (vence tudo)
ArchivedAt  != null → Arquivado
ConcludedAt != null → Concluído
senão               → Ativo
```

**Por que não uma coluna `Status` com o enum:** seriam duas fontes da verdade
para a mesma pergunta. "Arquivado" e "arquivado em 12/09" teriam de concordar
para sempre, e no dia em que discordassem não haveria como saber qual das duas
está certa. Derivar custa uma expressão; guardar custaria mais um invariante a
manter em cada caminho de escrita.

**As duas marcas são independentes de propósito.** Um checklist arquivado que
vai para a lixeira conserva `ArchivedAt`, então restaurá-lo da lixeira o devolve
ao **arquivo** — o estado em que ele estava — sem nenhum campo "estado
anterior". A precedência da lixeira sobre o arquivo é o que faz o usuário agir
onde o prazo está correndo.

**Não existe valor de enum para "excluído definitivamente".** É o estado
terminal do §9, e ele não é um estado da entidade: é a ausência dela. Um valor
que nenhuma linha do banco pode carregar seria código morto com cara de regra. O
que sobrevive é a trilha, e lá ele tem nome:
`TaskAuditOperation.PermanentlyDeleted`.

## `ConcludedAt` é derivado, mas persistido — e mantido pela raiz

O §2 manda contar o prazo da **data de conclusão**, não da criação. Isso exigiu
que as transições de ocorrência passassem a entrar pela raiz
(`TaskItem.CompleteOccurrence` e irmãs, no lugar de
`task.GetOccurrence(id).Complete(...)`): é a raiz que recalcula `ConcludedAt` na
mesma operação, então o campo não tem como divergir das ocorrências.

Persistir o derivado paga uma coisa concreta: a varredura vira um predicado
sobre índice parcial (`IX_Tasks_ReadyToArchive`) em vez de um `GROUP BY` sobre
`TaskOccurrences` a cada tique. **Cancelada não conta como concluída** — um
checklist do qual se desistiu não tem data de conclusão e portanto nunca é
arquivado sozinho.

**A migration faz backfill.** Sem ele, todo checklist concluído antes da
atualização ficaria com a coluna nula e jamais entraria no arquivamento
automático. Fazer isso na atualização só é seguro porque o arquivamento
automático **nasce desligado** (abaixo).

## Auditoria sem chave estrangeira — a decisão central do §8

`TaskAuditEntries` **não** tem FK para `Tasks`, e isso não é esquecimento: com
FK, o `DELETE` da exclusão definitiva levaria junto o registro dessa mesma
exclusão (cascata) ou passaria a falhar (sem cascata). O preço é que `TaskId`
pode apontar para nada — que é exatamente o que se quer dizer depois de um
`PermanentlyDeleted`. Pela mesma razão o **título é copiado**, não juntado: uma
trilha que só mostra GUIDs não serve para investigar nada.

A linha entra na **mesma unidade de trabalho** da operação auditada
(`RecordAsync` só rastreia; quem grava é o `SaveChanges` do caso de uso). Não
existe arquivamento sem registro, nem registro de algo que a regra recusou.

**Quem fez** é `AuditActor` (`User`/`System`) mais um nome opcional. É por isso
que "exclusão automática realizada pelo sistema" não precisou de operação
própria — e é `ICurrentUser` que responde o nome, com `UnknownUser` como padrão
degradado (`TryAddSingleton`, como o `TimeProvider.System`) e a conta do Windows
registrada pelo Desktop por cima.

## Arquivamento automático nasce desligado

Mesma razão do ADR-014 ("Upgrade não arma nada"): ligar sozinho faria a primeira
abertura depois da atualização varrer o histórico inteiro do usuário para fora
da lista, sem ninguém ter pedido. A lixeira não corre esse risco — numa
atualização ela está vazia —, então lá o prazo de 30 dias já vale. Consequência
boa: uma linha de configuração corrompida degrada para o padrão de fábrica, e o
padrão de fábrica **não varre nada**.
