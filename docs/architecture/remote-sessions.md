# Controle remoto: a conversa do Claude como uma sessão só

Fase 3 da orquestração remota. O objetivo é começar o Claude Code numa máquina
e acompanhar, aprovar, responder e continuar a mesma conversa de outra, do
celular para um computador e de um computador para outro.

Este documento junta a auditoria do que a fase 2 entregou, a comparação das
alternativas e a arquitetura-alvo. As entregas, com aceite e testes, estão no
[roadmap da fase 3](../roadmap/phase-3-remote-sessions.md). As decisões estão
nos ADR-091 a 094.

## 1. O fluxo de hoje

Da tarefa ao Claude, sem terminal (fase 2, [ADR-089](decisions/089-claude-code-remoto-headless-com-aprovacao.md)):

1. **Controlador** (desktop: *Controlar*; celular: aba **Remoto**). Monta o
   pedido em `RemoteRequests` (`Application/Remote/RemoteControl.cs`), assina
   com a chave do dispositivo e envia por `SendRemoteOperationHandler`.
2. **Servidor** (`Server.Application/RemoteOperations.cs`). Confere a
   assinatura, guarda a operação e toca a campainha do hub
   (`OperationAvailable`).
3. **Agente** (`MyTaskApp.Agent`). O `RemoteOperationScheduler` busca a fila.
   Depois o `RemoteOperationDispatcher` admite (`AdmitRemoteOperation`),
   escreve no diário e chama o executor.
4. **Executor do Claude** (`Application/Remote/AgentSessionExecutors.cs`):
   - o `HeadlessAgentPlanner` escolhe o worktree e o perfil;
   - o `HeadlessSessionStore` grava a `AgentSession` (modo `Headless`) no SQLite;
   - o `HeadlessPromptRunner` abre `claude -p --restricted` (um processo por
     prompt, `--resume` a partir do segundo) e transforma o `stream-json` em
     eventos.
5. **Aprovação.** O Claude chama o host de permissão (`--permission-prompt-tool`,
   o MCP do agente). O agente pede ao servidor uma `ApprovalRequest`, o
   controlador responde assinado, e o agente devolve `allow` ou `deny` ao
   Claude.
6. **Leitura.** O controlador lê o estado e os eventos de cada operação pelo
   REST, a cada 0,5 s a 1,5 s.

O Claude aberto pelo card da tarefa é outra coisa. Ele roda num terminal
interativo ([ADR-030](decisions/030-sessoes-claude-code-por-tarefa.md)) e é
acompanhado pelos hooks ([ADR-037](decisions/037-acompanhar-claude-code-pelos-hooks.md)).
Não passa pelo servidor.

## 2. O que a auditoria achou

### Defeitos confirmados (corrigidos na E1)

| # | Defeito | Evidência | Correção |
|---|---|---|---|
| 1 | A conversa ficava `Running` depois de o agente cair e recusava todo prompt | `HeadlessAgentPlanner.ContinueAsync` não reconciliava; o agente não roda o `AgentSessionMonitor` | `ContinueAsync` reconcilia (`AgentSessionReconciler.EndIfGone`); `HeadlessSessionRecovery` encerra as órfãs ao subir |
| 2 | Dois prompts rodavam na mesma conversa (`--resume` igual em paralelo) | sem trava por sessão; `Track` sobrescrevia | `HeadlessConversations.TryBegin` dá um `HeadlessPromptLease` por conversa; o segundo é recusado antes de gravar |
| 3 | O "terminou" chegava antes dos últimos eventos | o leitor lia os eventos e depois o estado; o estado final com páginas sobrando encerrava | estado primeiro, eventos depois; `IsFinished` só sem `HasMore` |
| 4 | "Permitir sempre nesta sessão" não era oferecido por nenhuma tela; se fosse, liberaria a ferramenta inteira | `ApprovalAnswers`, `RemoteControlViewModel`; a chave era só o nome | `HeadlessToolScope.RememberKey`: o comando exato do Bash; o composto sempre pergunta; botão nos dois controladores |
| 5 | A aprovação de operação terminada seguia pendente e respondível | `ListPendingAsync` não olhava a operação | fecha ao terminar; 410 na resposta; some da lista |
| 6 | O prompt num worktree com Claude aberto no terminal virava "Falhou nesta máquina" | `DbUpdateException` do índice único de sessão ativa | recusa com a mensagem certa, na conferência e na execução |
| 7 | `CreatedAt` sem limite contra o relógio | quebrava a premissa da janela de replay | `MaxClockSkew` de 5 min; `clock_skew` |
| 8 | O comentário de `AgentStopRequest` contradizia o código | `Remote.Contracts/Requests.cs` | corrigido |
| 9 | Um `agent.stop` entre a gravação e o `Track` deixava processo órfão | `HeadlessAgentRunner` | o lease tem token próprio; parar antes de abrir não abre; o callback que falha mata a árvore |

### Limitações de projeto

- **Sem sessão no servidor.** Cada prompt era uma operação solta, sem linha do
  tempo da conversa. Resolvido na E2 ([ADR-091](decisions/091-conversa-do-claude-como-sessao-no-servidor.md)).
- **A presença enganava.** O desktop mandava o mesmo sinal de vida do agente.
  Resolvido na E2 (`AgentSeenAt`).
- **Controladores só por tempo.** Ninguém ouvia o hub. Resolvido na E3
  ([ADR-094](decisions/094-controladores-ouvem-o-hub.md)).
- **A pergunta do Claude sem resposta de longe** (E4, [ADR-093](decisions/093-pergunta-do-claude-respondida-de-longe.md)).
- **A sessão do desktop invisível de longe** (E5, [ADR-092](decisions/092-sessao-gerenciada-a-partir-do-desktop.md)).
- **Sem push no celular** (E8).
- **Cancelamento sem assinatura** (R-08) e **retenção parcial**: operações,
  aprovações e pareamentos não são apagados (E9).

### Hipóteses não confirmadas

- O processo vivo (`--input-format stream-json` com `--replay-user-messages`)
  aceitaria instrução com o Claude respondendo. Não foi testado com o Claude de
  verdade; fica para a E6, atrás de um teste opt-in.

## 3. Alternativas

### Como falar com o Claude

| Opção | Interface oficial? | Prós | Contras | Decisão |
|---|---|---|---|---|
| **Headless, um processo por prompt (`-p`, `--resume`)** | sim | já existe, testado; aprovação pelo host de permissão; processo morre no fim | não recebe instrução no meio da resposta | **mantido** |
| Processo vivo (`--input-format stream-json`, `--replay-user-messages`) | sim | instrução no meio; confirmação de recebimento | processo longo para vigiar; mais estado no agente | avaliar na E6 |
| Remote Control nativo | não para terceiros | a experiência pronta do Claude | só com claude.ai e o app do Claude, assinatura, interativo | não |
| Channels | prévia | canal próprio | `--dangerously-load-development-channels`, diálogo interativo, ignorado em `-p` | não |
| Terminal real com leitura do texto | — | o que o usuário vê | o texto não é fonte da verdade; frágil | não |
| Agent SDK em outra linguagem | sim | API de biblioteca | outro runtime no agente; duplica o que o CLI já dá | não agora |

### Como avisar os controladores

| Opção | Decisão |
|---|---|
| Leitura por tempo (hoje) | fica como reserva |
| **SignalR (já no projeto, já toca o agente)** | **adotar na E3** (ADR-094) |
| WebSocket próprio | não: duplica o SignalR |

### Onde o processo roda

O Windows tem o terminal e o headless. No Linux, o headless funciona igual, e o
terminal não abre; a E9 trata disso.

## 4. A arquitetura-alvo, em passos

1. **E1 — defeitos (feito).** Sem mudar o fio: uma chave nova no `Details` da
   aprovação (`canRemember`, `rememberAs`).
2. **E2 — a conversa no servidor (feito, ADR-091).** O agente relata as
   conversas sem terminal (`PUT /remote/agent/sessions`), com versão e só ids
   e estado. Os controladores listam e leem a linha do tempo pelos mesmos
   handlers (`ListRemoteSessionsHandler`, `GetRemoteSessionTimelineHandler`).
   A presença do agente se separa da do desktop.
3. **E3 — tempo real** (ADR-094): os controladores ouvem o hub.
4. **E4 — a pergunta do Claude** (ADR-093).
5. **E5 — a sessão gerenciada a partir do desktop** (ADR-092).
6. **E6 — instrução com o Claude respondendo:** primeiro uma fila por
   conversa; depois, o processo vivo.
7. **E7 a E9 — linha do tempo estruturada, push e robustez.**

```
Celular / Desktop B ──REST (assinado)──▶ Servidor ◀──REST── Agente da máquina A
        ▲    (ListRemoteSessions,          │  RemoteAgentSessions   │  RemoteSessionPublisher
        │     GetRemoteSessionTimeline)     │  RemoteOperations      │  (a cada volta: estado
        └──────────── hub (E3) ◀────────────┘  RemoteOperationEvents │   + sinal de vida)
                                                                     ▼
                                                     claude -p --resume (um por prompt)
```

### O estado e onde ele mora

| Dado | Fonte da verdade | Cópia |
|---|---|---|
| A conversa (estado, atividade, prompt atual) | `AgentSession` no SQLite do executor | `RemoteAgentSessions` no servidor, pelo relato com versão |
| Os prompts e as respostas | eventos das operações no servidor (`RemoteOperationEvents`) | diário do agente até o ack |
| Título e branch | a tarefa sincronizada | nenhuma no servidor remoto |
| A presença do agente | `DeviceCapabilities.AgentSeenAt` | — |
| As aprovações | `RemoteApprovals` no servidor | — |

O texto do terminal não é fonte de nada. O processo não é a sessão: a conversa
sobrevive aos processos de cada prompt (`--resume`), e "agente sem notícias" é
estado desconhecido, nunca "terminou".

### Segurança

O que a E1 e a E2 mantiveram:

- **Nenhuma permissão nova do Claude.** O `--restricted` continua, e o
  `--dangerously-skip-permissions` continua proibido.
- **"Permitir nesta conversa" não alarga nada.** Lembra o comando exato; o
  comando composto ou com redirecionamento sempre pergunta.
- **O relato não leva caminhos nem comandos.** Também não leva o texto do
  Claude. A mensagem de atividade e a prévia do prompt vão mascaradas, com teto.
- **Só a máquina dona relata a conversa** (403 para as outras), e só um
  executor registrado relata (409).
- **Tokens nunca entram** em log, evento ou relato.
