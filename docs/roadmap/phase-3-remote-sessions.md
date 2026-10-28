# Fase 3 — A conversa do Claude como uma sessão só: roteiro

> **Estado:** E1 e E2 feitas; E3 a E9 planejadas. O desenho, a auditoria e a
> comparação das alternativas estão em
> [`../architecture/remote-sessions.md`](../architecture/remote-sessions.md).
> As decisões estão nos ADR-091 a 094 do [índice](../architecture/README.md).

## Princípios

- **Tudo vale nos dois sentidos.** Do celular para um computador e de um
  computador para outro. Toda tela de controle usa os mesmos handlers da
  Application.
- **Sem reescrever nem trocar tecnologia.** O headless, o REST e o SignalR já
  existem.
- **O fio só cresce.** Rotas e tipos novos, chaves novas nos `JsonElement`
  livres, códigos de erro novos; nunca um campo novo em tipo existente nem um
  valor novo de enum (o `RemoteJson` é estrito).
- **Nenhuma permissão nova para o Claude.** O texto do terminal não é fonte da
  verdade, e o processo não é a sessão.

| Entrega | Resultado visível | Depende de | Estado |
|---|---|---|---|
| E1 | os defeitos da fase 2 corrigidos; "Permitir nesta conversa" nos dois controladores | — | ✔ |
| E2 | as conversas da máquina, com a linha do tempo, no celular e em outro desktop; agente parado visível | E1 | ✔ |
| E3 | a saída e as aprovações na hora, pelo hub | E2 | planejada |
| E4 | responder a pergunta do Claude de longe | E1 | planejada |
| E5 | começar no computador e continuar no celular | E2 | planejada |
| E6 | instrução com o Claude respondendo | E2 | planejada |
| E7 | linha do tempo em cartões no celular | E2 | planejada |
| E8 | push com o app fechado | E3 | planejada |
| E9 | robustez: cancelamento assinado, retenção, Linux | — | planejada |

---

## E1 — Defeitos confirmados ✔

**Problema:** a lista da auditoria, itens 1 a 9 (veja a
[seção 2](../architecture/remote-sessions.md#2-o-que-a-auditoria-achou)).

**Arquivos:**

- `Remote.Contracts`: `RemoteLimits.MaxClockSkew`, `RemoteErrorCodes.ClockSkew`,
  `RemoteOperationRules`.
- `Server.Application`: `RemoteOperations`, `RemoteApprovals`.
- `Application/Remote`:
  - `AgentSessionExecutors.cs`: `HeadlessConversations`, `HeadlessPromptLease`,
    `HeadlessSessionRecovery`;
  - `HeadlessToolScope.cs`;
  - `RemoteControl.cs` (o leitor).
- `Domain/Agents/AgentSession.EndHeadlessPrompt`.
- `Infrastructure/Processes/HeadlessAgentRunner`.
- As telas de aprovação do desktop e do celular.

**Migrations:** nenhuma.

**Testes:**

- `RemoteOperationRulesTests`, `AdmitRemoteOperationTests`;
- `RemoteControlTests`, `RemoteControlViewModelTests`;
- `RemoteTests` (celular);
- `RemoteApprovalTests` e `RemoteOperationTests` (integração);
- `AgentSessionTests`, `AgentSessionExecutorsTests`;
- `HeadlessAgentRunnerTests`, `HeadlessToolScopeTests`.

**Aceite:**

- O agente reiniciado no meio de um prompt aceita o próximo.
- Dois prompts juntos: um roda e o outro é recusado com a mensagem.
- A última linha sempre chega.
- "Permitir nesta conversa" funciona do celular e do desktop e não libera outro
  comando.
- A aprovação de operação terminada some.

**Desvios do plano:**

- **"Permitir nesta conversa" lembra o comando exato**, e não os dois primeiros
  termos. `dotnet test` não libera `dotnet run`.
- **O reconciliador do desktop não ignora as sessões headless.**
  `EndHeadlessPrompt` torna inofensivo o fechamento por qualquer um dos dois:
  o resultado do prompt é gravado mesmo se a sessão já foi encerrada, e a
  sessão parada não volta.

## E2 — A conversa no servidor ✔ ([ADR-091](../architecture/decisions/091-conversa-do-claude-como-sessao-no-servidor.md))

**Problema:**

- Sem linha do tempo da conversa: cada prompt era uma operação solta, e a tela
  esquecia a conversa ao fechar.
- A presença enganava: o desktop ligado parecia agente ligado.

**Arquivos:**

- `Remote.Contracts/Sessions.cs`; `RemoteSchema` (rotas e `SessionChanged`).
- `Server.Application/RemoteAgentSessions.cs`; `RemoteOperations.SessionOf`.
- `Server.Infrastructure`:
  - `EfRemoteAgentSessionStore`;
  - `ServerRows` (`RemoteAgentSessionRow`, `AgentSeenAt`, `AgentSessionId`).
- `Server.Api`: `RemoteApiEndpoints`; a varredura em `ServerSupport`.
- `Application/Remote/RemoteSessions.cs`:
  - `IRemoteSessionsApi`, `RemoteSessionPublisher`, `RemoteSessionReports`;
  - `ListRemoteSessionsHandler`, `GetRemoteSessionTimelineHandler`;
  - `RemoteSessionTexts`.
- `Application/Remote/RemoteOperationScheduler` (o relato a cada volta);
  `IAgentSessionRepository.ListHeadlessAsync`; a prévia do prompt em
  `AgentSessionExecutors`.
- `Infrastructure`: `RemoteApiClient` e `AgentSessionRepository`.
- Desktop: `RemoteSessionsViewModel` e o card "Conversas do Claude" na janela
  *Controlar*.
- Celular:
  - `RemoteSessionViewModel` e `RemoteSessionPage` (rota `session`);
  - a seção na aba Remoto;
  - `RemoteSender` com `open: false`.

**Migrations:** `RemoteAgentSessions` no servidor, só aditiva (a tabela, duas
colunas que aceitam nulo e índices). Nenhuma no SQLite.

**Testes:**

- Integração:
  - `RemoteAgentSessionTests`;
  - o ponta a ponta `AnotherDesktop_ListsTheConversationOfTheNotebook_AndReadsItsTimeline`.
- Application:
  - `RemoteSessionsTests`;
  - `RemoteOperationSchedulerTests`: o relato a cada volta, e o relato que
    falha não para a fila;
  - a prévia em `AgentSessionExecutorsTests`.
- Infraestrutura: `AgentSessionPersistenceTests`, a lista headless.
- Desktop: `RemoteControlViewModelTests`.
- Celular: `RemoteTests`.

**Aceite:**

- O celular e um segundo desktop listam as conversas, abrem uma e veem uma
  linha do tempo única. Mandam o próximo prompt e param.
- A máquina com o agente parado aparece assim.
- A conversa com o agente sem notícias aparece como "estado desconhecido".
- O servidor antigo degrada com a mensagem.

**Desvio do plano:** sem tabela local de relatos. A versão é o instante da
mudança, e o que já foi relatado fica na memória (veja as alternativas do
ADR-091).

**Riscos aceitos:**

- O estado na lista atrasa até uma volta do agente.
- A linha do tempo de uma conversa longa lê uma página por operação.

## E3 — Tempo real nos controladores ([ADR-094](../architecture/decisions/094-controladores-ouvem-o-hub.md))

**Problema:** os controladores leem por tempo; o hub só toca o agente.

**Arquivos:**

- `Infrastructure/Remote/SignalRRemoteHub` (os avisos dos controladores);
- `RemoteControlViewModel` e `RemoteSessionsViewModel`;
- `RemoteOperationViewModel` e `RemoteSessionViewModel`;
- `ApprovalWatcher`.

**Testes:**

- `RemoteHubTests`: os avisos chegam ao controlador;
- testes de tela com um hub de mentira: o aviso dispara a leitura; sem hub, a
  leitura por tempo recua até 5 s; na reconexão, a leitura parte do cursor.

**Aceite:** a saída aparece em menos de 1 s com a tela aberta, e nada se perde
com o hub caindo no meio.

**Riscos:** uma conexão a mais por controlador aberto.

## E4 — A pergunta do Claude ([ADR-093](../architecture/decisions/093-pergunta-do-claude-respondida-de-longe.md))

**Problema:** o `AskUserQuestion` fica fora dos perfis.

**Arquivos:**

- os perfis (`AgentToolProfiles`);
- `HeadlessPromptRunner.DecideAsync` (`updatedInput.answers`);
- `RemoteApprovalTexts`;
- os cartões de opção no desktop e no celular.

**Testes:**

- a pergunta chega com as opções;
- a resposta assinada volta como `updatedInput`;
- o controlador antigo vê uma aprovação comum.

**Aceite:** responder a pergunta do celular e do desktop, e o Claude seguir com
a resposta.

## E5 — Sessão gerenciada a partir do desktop ([ADR-092](../architecture/decisions/092-sessao-gerenciada-a-partir-do-desktop.md))

**Problema:** o Claude aberto pelo card não é visível de longe.

**Arquivos:**

- `AdmitRemoteOperation` (confiar na chave do próprio dispositivo);
- o card da tarefa ("Abrir Claude gerenciado");
- o modelo de ameaças.

**Testes:**

- o próprio dispositivo é admitido, e outro sem pareamento não é;
- a conversa aberta no desktop aparece no celular (ponta a ponta).

**Aceite:** começar no computador e mandar o próximo prompt do celular.

**Dependência:** E2.

## E6 — Instrução com o Claude respondendo

**Problema:** o prompt seguinte é recusado enquanto o anterior roda.

**Passos:**

1. Uma fila por conversa no agente: o prompt espera, em vez de ser recusado.
2. Avaliar o processo vivo (`--input-format stream-json` com
   `--replay-user-messages`), atrás de um teste opt-in com o Claude de verdade.

**Aceite:** a instrução mandada no meio é entregue depois da resposta atual,
na ordem.

## E7 — Linha do tempo estruturada no celular

Cartões por tipo de evento, os `Details` da aprovação e o resultado das
ferramentas (hoje o resultado de sucesso é descartado).

## E8 — Push

Um ADR entre FCM e push próprio; a aprovação com o app fechado.

## E9 — Robustez

- O cancelamento assinado (R-08).
- A retenção de operações, aprovações e pareamentos.
- O ADR-085, item 3.
- O limite atômico de 20 pedidos por executor.
- O terminal no Linux.
