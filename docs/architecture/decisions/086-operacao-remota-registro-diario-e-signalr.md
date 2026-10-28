# ADR-086 — Operação remota: registro no PostgreSQL, diário no agente, SignalR só como campainha

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | remoto |
| **Registrado** | 2026-10-24, commit `36eb99c` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | RemoteOperations, RemoteOperationEvents, RemoteOperationJournal, RemoteOperationStatus, Seq, NotAfter, /hubs/remote, SignalR |
| **Relações** | revisto por: [ADR-091](091-conversa-do-claude-como-sessao-no-servidor.md), [ADR-094](094-controladores-ouvem-o-hub.md); relacionado: [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md), [ADR-084](084-orquestracao-remota-coordenador-e-agente.md), [ADR-085](085-agente-sem-tela-sobre-o-mesmo-sqlite.md) |
| **Código** | `src/MyTaskApp.Remote.Contracts/Operations.cs`, `src/MyTaskApp.Remote.Contracts/RemoteOperationLifecycle.cs`, `src/MyTaskApp.Remote.Contracts/Events.cs`, `src/MyTaskApp.Remote.Contracts/RemoteLimits.cs`, `src/MyTaskApp.Application/Remote/RemoteOperationDispatcher.cs`, `src/MyTaskApp.Application/Remote/RemoteOperationScheduler.cs`, `src/MyTaskApp.Server.Application/RemoteOperations.cs` |

**Estado:** aceito (fase 2), implementado: o registro no PostgreSQL com os eventos numerados, o diário do agente, o hub como campainha e a busca de 30 s sem ele. Os casos de falha têm teste ponta a ponta (T-35); o servidor entrega os eventos só até o primeiro buraco.

**Contexto:** uma operação pode durar horas (uma sessão do Claude, uma cópia de
banco). A rede cai, o celular dorme, o agente reinicia, o servidor é
atualizado. O estado não pode depender de uma conexão aberta. O sync (ADR-061)
só faz polling a cada 60 s, e uma aprovação de ferramenta do Claude esperando
um minuto é inviável.

**Decisão:**

1. **A verdade é durável dos dois lados.** No servidor, `RemoteOperations`
   (pedido assinado, estado, `NotAfter`, `CancelRequestedAt`) e
   `RemoteOperationEvents` (`(OperationId, Seq)` único). No agente,
   `RemoteOperationJournal` e os eventos ainda sem confirmação, no SQLite e
   fora do `SyncRegistry`.
2. **Idempotência em toda escrita.** Criar: `OperationId` (UUIDv7 do
   controlador) com o hash do pedido, como o `ProcessedOperations`. Aceitar: a
   linha do diário existe antes da resposta. Eventos: `Seq` denso gerado pelo
   agente; o servidor ignora repetidos e devolve o maior contíguo; o agente só
   apaga o que teve confirmação.
3. **Estados:** `Queued`, `Dispatched`, `Accepted`, `Running`,
   `WaitingApproval`, `Succeeded`, `Failed`, `Canceled`, `Rejected`, `Expired`,
   `Interrupted`. O servidor decide até o aceite; depois, o agente, com a
   transição conferida por uma tabela e regressão recusada. Pedido de
   cancelamento é carimbo, não estado.
4. **Tempo de vida.** `NotAfter` (padrão 10 min, teto 24 h): máquina desligada
   não roda amanhã o que foi pedido hoje. Despacho sem aceite volta à fila em
   2 min. Nada se repete sozinho: "Repetir" cria outra operação, com `RetryOf`.
5. **Checkpoints** nas operações compostas: cada etapa grava o fim no diário.
   Depois de cair, o agente retoma o que é retomável (preparar; worktree, por
   `AdoptExisting`) e marca `Interrupted` o que não é (cópia de banco, comando
   do usuário).
6. **SignalR (`/hubs/remote`) só avisa**: há operação, pediram cancelamento,
   chegaram eventos, há aprovação. Sem o hub, o agente busca por REST a cada
   30 s. Uma instância de servidor dispensa backplane.

## Alternativas

- **Só polling**, como o sync. Simples, mas aprovação e saída ao vivo ficariam
  com até um minuto de atraso.
- **Long polling ou SSE próprios.** Sem pacote novo, mas é protocolo para
  manter à mão (reconexão, keep-alive), e o SSE não tem cliente pronto no MAUI.
- **WebSocket cru.** Mesmo custo do anterior, sem fallback.
- **Estado na conexão** (o hub como fonte da verdade). Uma queda perderia
  eventos e aprovações; o servidor reiniciado esqueceria o que rodava.

## Consequências

- Pacote novo só nos clientes: `Microsoft.AspNetCore.SignalR.Client`. O
  servidor já tem o SignalR no framework.
- O proxy (Caddy/Traefik) precisa repassar WebSocket, e a query string do hub
  (que leva o token) sai do log.
- Os eventos ocupam o PostgreSQL: teto de 1 MiB por operação e retenção de
  30 dias.

## Fora de escopo, de propósito

- Ordem global entre operações de máquinas diferentes.
- Retomar o processo do SO depois de o agente cair: o processo morre junto
  (Job Object no Windows, grupo de processos no Linux).
