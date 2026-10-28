# Fase 3 — A conversa do Claude como uma sessão só: roteiro

> **Estado:** E1 a E6 feitas; E7 a E9 planejadas. O desenho, a auditoria e a
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
| E3 | a saída e as aprovações na hora, pelo hub | E2 | ✔ |
| E4 | responder a pergunta do Claude de longe | E1 | ✔ |
| E5 | começar no computador e continuar no celular | E2 | ✔ |
| E6 | instrução com o Claude respondendo | E2 | ✔ |
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

## E3 — Tempo real nos controladores ✔ ([ADR-094](../architecture/decisions/094-controladores-ouvem-o-hub.md))

**Problema:** os controladores leem por tempo; o hub só toca o agente.

**Arquivos:**

- `Application/Remote/RemoteWatch.cs`: `IRemoteWatch`, `RemoteNotice`,
  `RemotePace`.
- `Infrastructure/Remote/SignalRRemoteWatch`; `RemoteHubConnection`, dividida
  com a campainha do agente.
- Desktop: `RemoteControlViewModel` e `RemoteSessionsViewModel`; a janela
  solta o hub ao fechar.
- Celular: `RemoteOperationViewModel`, `RemoteSessionViewModel` e
  `ApprovalWatcher`.

**Migrations:** nenhuma.

**Testes:**

- `RemotePaceTests`:
  - o aviso acorda;
  - o relógio vira reserva de 5 s;
  - o aviso de outra coisa não acorda, e o "religou" sempre acorda;
  - cada tela segura a conexão.
- `RemoteControlViewModelTests`: a saída lida no aviso e a cada 5 s sem ele.
- `RemoteTests` (celular): o prompt de outra tela e a aprovação nova aparecem
  no aviso, sem o relógio andar.
- Ponta a ponta: `AnotherDesktop_HearsTheOutputAndTheConversationChange_ThroughTheHub`,
  com a escuta de verdade contra o servidor.

**Aceite:** a saída aparece em menos de 1 s com a tela aberta (o relógio do
desktop é de 0,5 s; o celular acorda no aviso). Nada se perde com o hub
caindo no meio: na volta, tudo é relido do cursor.

**Riscos:** uma conexão a mais por controlador aberto; o desktop e o agente da
mesma máquina abrem uma cada.

## E4 — A pergunta do Claude ([ADR-093](../architecture/decisions/093-pergunta-do-claude-respondida-de-longe.md))

**Problema:** o `AskUserQuestion` fica fora dos perfis.

**Arquivos:**

- `HeadlessApproval` (a ferramenta nos perfis, e `ToolPermissionDecision`
  com `UpdatedInput`), `ApprovalMcpServer` (devolve a entrada nova);
- `HeadlessQuestions` (ler, mascarar, assinar e traduzir as respostas);
- `AgentSessionExecutors.AskAsync`; `AnswerApproval.Answers` e
  `RemoteApprovalResult.Answers`;
- `RemoteApprovalTexts` (os rótulos e a pergunta numa linha);
- desktop: `RemoteQuestionItem` e a janela *Controlar*; celular:
  `ApprovalAnswers` e as três páginas.

**Migrations:** nenhuma. O fio não muda: as respostas vão no
`TypedConfirmation` assinado.

**Testes:**

- `HeadlessQuestionsTests`: a forma aceita, a máscara, as respostas pela ordem,
  a escrita, o que não responde tudo;
- `AgentSessionExecutorsTests`: a pergunta chega com as opções; a resposta
  volta como `updatedInput`; negar, vencer ou permitir sem resposta deixa o
  Claude decidir; a pergunta fora de forma nem é feita;
- `RemoteApprovalsTests`: as respostas vão assinadas e o agente as lê;
- `RemoteControlViewModelTests` e `RemoteTests` (celular): marcar, escrever,
  desistir;
- opcional, com o Claude de verdade: `ApprovalClaudeTests`.

**Aceite:** responder a pergunta do celular e do desktop, e o Claude seguir com
a resposta.

## E5 — Sessão gerenciada a partir do desktop ([ADR-092](../architecture/decisions/092-sessao-gerenciada-a-partir-do-desktop.md))

**Problema:** o Claude aberto pelo card não é visível de longe.

**Arquivos:**

- `RemoteTrust` (os pareados mais a própria chave), na admissão e nas
  respostas de aprovação do `RemoteOperationDispatcher`;
- o card da tarefa: "Abrir Claude gerenciado" (`RemotePrepareViewModel`,
  `TaskDevelopmentsViewModel`), que abre *Controlar* desta máquina no
  ambiente da tarefa;
- o modelo de ameaças (RS-09 e R-11).

**Migrations:** nenhuma. O servidor já aceitava um pedido para o próprio
dispositivo; quem recusava era o agente.

**Testes:**

- `AdmitRemoteOperationTests`: a própria máquina é admitida e roda como
  "este computador"; o id dela com outra chave, ou a chave dela dizendo ser
  outro, não; nada sem aceitar controle; a resposta dela vale como aprovação;
- `RemotePrepareViewModelTests`: o botão abre *Controlar* desta máquina no
  ambiente da tarefa, avisa quando o ambiente ainda não chegou ao agente, e
  some sem aceitar controle;
- ponta a ponta: `AConversationStartedOnTheDesktop_IsContinuedFromThePhone`.

**Aceite:** começar no computador e mandar o próximo prompt do celular.

**Dependência:** E2.

## E6 — Instrução com o Claude respondendo

**Problema:** o prompt seguinte é recusado enquanto o anterior roda.

**Passos:**

1. Uma fila por conversa no agente: o prompt espera, em vez de ser recusado
   ([ADR-095](../architecture/decisions/095-fila-de-prompts-por-conversa.md)).
2. Avaliar o processo vivo (`--input-format stream-json` com
   `--replay-user-messages`), atrás de um teste opt-in com o Claude de verdade.

**Feito:**

- `HeadlessConversations.WaitAsync`: a vez na ordem de chegada, um esperando no
  máximo, a parada dispensa a fila, o cancelamento sai dela;
- o aviso "Na fila: …" na linha do tempo; `CanPrompt` aceita a conversa
  respondendo, no desktop e no celular;
- o processo vivo: avaliado e não adotado (ADR-095, item 5). O teste
  `LiveProcessClaudeTests` guarda o comportamento; com o Claude sem login nesta
  máquina, ele foi pulado, então o recibo de entrega não foi visto rodando.

**Testes:** `AgentSessionExecutorsTests` (o segundo espera e roda depois; o
terceiro é recusado; parar dispensa; cancelar sai da fila),
`RemoteSessionsTests` (`CanPrompt`).

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
