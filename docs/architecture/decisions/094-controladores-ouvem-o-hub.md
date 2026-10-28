# ADR-094 — Os controladores ouvem o hub, com a leitura por tempo como reserva

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | remoto |
| **Registrado** | 2026-10-28 |
| **Palavras-chave** | SignalR, EventsAppended, OperationChanged, ApprovalRequested, SessionChanged, polling, backoff, reconexão |
| **Relações** | revisa: [ADR-086](086-operacao-remota-registro-diario-e-signalr.md); relacionado: [ADR-091](091-conversa-do-claude-como-sessao-no-servidor.md), [ADR-096](096-verificacao-periodica-no-celular.md) (com o app fechado) |
| **Código** | `src/MyTaskApp.Application/Remote/RemoteWatch.cs`, `src/MyTaskApp.Infrastructure/Remote/SignalRRemoteWatch.cs`, `src/MyTaskApp.Desktop/ViewModels/RemoteControlViewModel.cs`, `src/MyTaskApp.Mobile.Core/ViewModels/RemoteSessionViewModel.cs` |

**Estado:** implementado (fase 3, E3).

- A porta `IRemoteWatch` entrega os avisos da conta.
- O `RemotePace` decide quando cada tela lê.
- A `SignalRRemoteWatch` divide com a campainha do agente a montagem da
  conexão. Ela fica de pé enquanto houver uma tela remota aberta.

Usam a escuta, no desktop, a janela *Controlar* e, no celular, a tela da
operação, a da conversa e a lista de aprovações.

**Contexto:** o desktop e o celular leem por tempo, de 0,5 s a 1,5 s com a tela
aberta. Isso gasta requisição com a tela parada e atrasa o que chega entre
duas leituras. O hub já é a campainha do agente (ADR-086).

**Decisão (proposta):**

1. Os controladores conectam ao hub enquanto uma tela remota está aberta. O
   aviso dispara a leitura do que mudou, pelos mesmos handlers de hoje.
2. O hub continua só campainha: o aviso não traz dado, só o id. Quem lê é o
   REST, com o cursor de cada operação.
3. Com o hub ligado, a leitura por tempo vira reserva, a cada 5 s, para o
   aviso que se perdeu. Sem o hub (proxy que barra, rede ruim), a leitura por
   tempo continua no intervalo de cada tela, como antes. Na reconexão, um
   aviso "religou" faz reler tudo do cursor guardado: nada se perde.

**Por quê:** o SignalR já está no projeto e no servidor. Não há tecnologia
nova, e o REST continua a fonte da verdade.

## Alternativas

- **WebSocket próprio.** Duplica o que o SignalR já faz, com reconexão e
  transporte de reserva.
- **Só leitura por tempo, mais lenta.** Piora o "ao vivo" sem resolver o custo.

## Consequências

- A saída e as aprovações aparecem na hora com a tela aberta.
- Mais uma conexão por controlador aberto; o servidor já limita por
  dispositivo.

## Fora de escopo, de propósito

- O push com o app fechado (fase 3, E8).
