# ADR-090 — Cópia de banco de longe: as mesmas barreiras, e produção com aprovação digitada

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | remoto |
| **Registrado** | 2026-10-24, commit `36eb99c` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | database.copy, DatabaseCopyRequest, ApprovalKind.ProductionDatabase, TypedConfirmation, CriticalProduction, RunDatabaseCopyHandler, WaitingApproval |
| **Relações** | relacionado: [ADR-056](056-operacoes-de-banco-politica-central.md), [ADR-060](060-banco-do-ambiente-variavel-database.md), [ADR-082](082-script-depois-da-copia.md), [ADR-085](085-agente-sem-tela-sobre-o-mesmo-sqlite.md), [ADR-088](088-catalogo-fechado-de-operacoes-remotas.md) |
| **Código** | `src/MyTaskApp.Remote.Contracts/Requests.cs`, `src/MyTaskApp.Remote.Contracts/Approvals.cs`, `src/MyTaskApp.Application/DatabaseOperations/CopyHandlers.cs`, `src/MyTaskApp.Application/Remote/DatabaseCopyExecutor.cs` |

**Estado:** aceito (fase 2), implementado (T-31 a T-33): a cópia de longe pelo mesmo `RunDatabaseCopyHandler`, só entre conexões com `RemoteAllowed`, a produção como `ApprovalKind.ProductionDatabase` com o nome digitado na crítica, e o "Preparar tudo", que valida e aprova antes do Git.

**Contexto:** o ADR-056 pôs a cópia de produção atrás de quatro barreiras
(política, fluxo, processo, PostgreSQL só leitura), e a confirmação de produção
é um diálogo na tela (`ProductionConfirmed`, `TypedConfirmation`). De longe,
não há tela na máquina executora. O `DatabaseOperationGate` e a recuperação de
interrompidas são do processo (ADR-085 trata).

**Decisão:**

1. `database.copy` só com ids do catálogo: conexões com `RemoteAllowed` (coluna
   local, desligada por padrão). Nenhuma operação remota cria, edita ou lê
   conexão, senha ou perfil.
2. O agente chama o **mesmo** `RunDatabaseCopyHandler`/
   `CopyDevelopmentDatabaseHandler`: política, `Demand` em cada etapa,
   `PostgresProcessGuard` e auditoria inalterados.
3. **A confirmação vira aprovação.** Quando a política pede confirmação, o
   agente abre um `ApprovalRequest` (`ProductionDatabase`) com origem, destino,
   perfil de anonimização e tabelas sem regra. `CriticalProduction` exige o
   nome do banco digitado na resposta assinada.
4. A auditoria (`DatabaseOperationAudit`) registra o dispositivo que assinou.

## Alternativas

- **Recusar produção de longe.** Mais simples, mas o caso de uso principal da
  cópia (ADR-060) é justamente produção anonimizada para o ambiente.
- **Confirmação no pedido**, e não depois da política. O controlador não sabe
  o que a política vai exigir; quem sabe é o executor, com as conexões dele.

## Consequências

- Uma cópia remota de produção sempre para em `WaitingApproval`, mesmo pedida
  pelo próprio usuário. É de propósito.

## Fora de escopo, de propósito

- Dump simples, restauração arbitrária, SQL livre e o script pós-cópia
  (ADR-082) disparados de longe sem passar pela cópia.
