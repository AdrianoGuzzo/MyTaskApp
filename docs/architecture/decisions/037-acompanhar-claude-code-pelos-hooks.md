# ADR-037 — Acompanhar o Claude Code pelos hooks dele

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | agente-ia |
| **Registrado** | 2026-09-26, commit `23a1665` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | AgentEventListener, ClaudeCodeHookEvents, AgentActivity, RecordAgentEventHandler, hooks, HookTokenHash, AgentAlertWindow, 47831 |
| **Relações** | relacionado: [ADR-030](030-sessoes-claude-code-por-tarefa.md), [ADR-031](031-varios-ambientes-por-tarefa.md), [ADR-033](033-parametros-do-agente-lembrados.md), [ADR-042](042-som-por-estado-do-claude-code.md), [ADR-059](059-servidor-mcp-local-loopback.md), [ADR-081](081-consulta-inteligente-somente-leitura.md), [ADR-089](089-claude-code-remoto-headless-com-aprovacao.md) |
| **Código** | `src/MyTaskApp.Infrastructure/Agents/AgentEventListener.cs`, `src/MyTaskApp.Infrastructure/Agents/ClaudeCode/ClaudeCodeHookEvents.cs`, `src/MyTaskApp.Infrastructure/Agents/ClaudeCode/ClaudeCodeHooks.cs`, `src/MyTaskApp.Application/Agents/RecordAgentEvent.cs` |

**Contexto:** o ADR-030 liga a tarefa ao **processo** do Claude: o app sabe se
ele está aberto, mas não o que ele está fazendo. Quem deixa o Claude
trabalhando e vai fazer outra coisa só descobre que ele parou numa pergunta, ou
que terminou, voltando ao terminal. O pedido: avisar quando o Claude precisar
do usuário ou terminar e estiver aguardando revisão.

**Decisão:** o app acompanha a sessão pelos **hooks oficiais** do Claude Code.
Cada evento que interessa vira um POST para uma porta local do MyTaskApp, e a
sessão ganha uma **atividade**: `Working`, `WaitingForUser`, `WaitingReview` ou
`Failed`. Quando ela passa a esperar o usuário, aparece um aviso no canto da
tela e o selo da linha muda.

```
MyTaskApp ── abre ──► claude --settings <hooks do app> …   (env: MYTASKAPP_*)
                         │ hook http (evento em JSON)
                         ▼
          127.0.0.1:47831/api/claude/events  (AgentEventListener)
                         │ fila, na ordem de chegada
                         ▼
          RecordAgentEventHandler ─► AgentSession.Activity ─► selo, card, aviso
```

**Nunca pelo texto do terminal.** O estado sai só de campos estruturados do
hook (`hook_event_name`, `notification_type`, `tool_name`). "Terminou" ou um
"?" no fim da resposta não decidem nada. O texto (a pergunta, a última
resposta) vai junto só para o aviso mostrar. A tradução mora num lugar só,
`ClaudeCodeHookEvents`, e daí para dentro ninguém conhece nome de evento do
Claude.

| Hook do Claude | Evento (`AgentEventType`) | Atividade |
|---|---|---|
| `UserPromptSubmit`, `PostToolUse` | `Working` | Trabalhando |
| `PreToolUse` de `AskUserQuestion` / `ExitPlanMode` | `NeedsUserInput` | Aguardando você |
| `Notification` `permission_prompt`, `elicitation_*`, `agent_needs_input` | `NeedsUserInput` | Aguardando você |
| `Stop` | `ResponseCompleted` | Aguardando revisão |
| `StopFailure` | `SessionFailed` | Erro na resposta |
| `Notification` (os outros tipos) | `Notification` | não muda |
| `TaskCompleted` | `TaskCompleted` | não muda |
| `SessionEnd` | `SessionStopped` | não muda |

**`Stop` não é o fim da sessão.** É o fim de uma resposta: o Claude continua
aberto esperando a próxima mensagem. Por isso vira "aguardando revisão", e não
"concluída". O fim da sessão continua sendo o fim do **processo** (ADR-030). O
`SessionEnd` não encerra nada: o `/clear` também manda um, e o Claude continua
aberto. `SubagentStop` fica de fora: um subagente terminar não termina a
resposta, e o `Stop` do agente principal vem depois. `SessionStart` também: o
Claude não dispara hook HTTP nele. No teste com o Claude real, o hook de
comando disparou e o HTTP não. O `session_id` vem em todo evento, então nada
se perde. O `SessionEnd` HTTP chega quando cabe no orçamento de 1,5 s do
encerramento.

**Os estados pedidos, e onde cada um mora.** Não há um enum novo de status.
São duas dimensões que já existiam separadas: o processo (`AgentSessionStatus`,
ADR-030) e a atividade (`AgentActivity`, este ADR).

| Estado | Aqui |
|---|---|
| NotStarted | sem sessão (card "Idle") |
| Starting | `Status = Starting` |
| Running | `Running` + `Working` (ou `Unknown`, sem aviso ainda) |
| WaitingForUser | `Running` + `WaitingForUser` |
| WaitingReview | `Running` + `WaitingReview` |
| Completed / Stopped | `Exited` — o processo acabou (ADR-030 continua sem "Parar") |
| Failed | `Failed` (o terminal não abriu) ou atividade `Failed` (a resposta falhou) |

**Hooks por execução, e não na configuração do usuário.** O app não edita o
`~/.claude/settings.json`. Os hooks moram num arquivo do próprio app,
`%APPDATA%\MyTaskApp\agents\claude-code-hooks.json`, passado com
`claude --settings <arquivo>` só aos Claude que o app abre. O Claude **soma**
os hooks de todas as fontes (usuário, projeto, local e `--settings`), então os
do usuário continuam rodando junto. Isso foi conferido com o Claude real: um
hook de projeto e os hooks do app dispararam na mesma resposta. Assim:

- nenhum arquivo do usuário é escrito, e não há merge para errar nem hook dele
  para sobrescrever;
- não há o que desinstalar: desligar o acompanhamento é não passar o arquivo;
- um Claude aberto à mão, fora do app, nem fica sabendo que o MyTaskApp existe.
  Um hook global dispararia em toda sessão do usuário, e mandaria prompts de
  sessões alheias para uma porta local;
- o arquivo tem **só** a chave `hooks`: o `--settings` sobrepõe chave a chave,
  e qualquer outra chave passaria por cima da escolha do usuário.

O `--settings` vem antes dos parâmetros do usuário. Se ele passar o próprio
`--settings`, o dele vale: perde-se o acompanhamento, e não a configuração dele.

**A configuração do usuário é lida, nunca escrita.** Antes de abrir,
`ClaudeCodeHooks.UnavailableReason` confere, na ordem de precedência do Claude
(gerenciada, usuário, projeto, local), o que faria os hooks não rodarem:
`disableAllHooks`, `allowManagedHooksOnly` na política gerenciada e
`allowedHttpHookUrls` sem a URL do app. Nesses casos o Claude abre do mesmo
jeito, sem acompanhamento, e o card diz o motivo.

**Hook HTTP, direto para a porta local.** Não há processo intermediário. A
URL fica fixa no arquivo. O que identifica a sessão vem nos cabeçalhos, a
partir do ambiente do processo (`allowedEnvVars`). O Claude só interpola
variáveis nos cabeçalhos, não na URL. O arquivo é um só para todas as sessões
e não guarda segredo nenhum.

**A associação é explícita, e conferida.** O app põe no ambiente do processo
`MYTASKAPP_TASK_ID`, `MYTASKAPP_DEVELOPMENT_ID` (o ambiente/repositório,
ADR-031), `MYTASKAPP_AGENT_SESSION_ID`, `MYTASKAPP_WORKTREE_PATH`,
`MYTASKAPP_BRANCH`, `MYTASKAPP_EVENTS_URL` e `MYTASKAPP_HOOK_TOKEN`. O segredo
é aleatório (32 bytes) e **por sessão**. O banco guarda só o SHA-256
(`AgentSessions.HookTokenHash`), comparado em tempo constante. Aviso sem o
segredo daquela sessão, de sessão desconhecida, de sessão sem acompanhamento
ou com tarefa que não bate é recusado. Qualquer processo do computador alcança
a porta, e é o segredo que separa o Claude que o app abriu de quem só sabe o
endereço. A pasta (`cwd`) não decide nada; fora do worktree, só vai para o
log.

**A porta (`AgentEventListener`).**

- `TcpListener` em `IPAddress.Loopback`, com HTTP mínimo: POST com
  `Content-Length` ou em pedaços, uma requisição por conexão, corpo até 1 MB.
  O `HttpListener` passaria pelo http.sys, que pede reserva de URL. O Kestrel
  traria o ASP.NET inteiro para um app de bandeja.
- Pedido com `Origin` (navegador) ou com `Host` que não seja
  `127.0.0.1`/`localhost` na porta dele (DNS rebinding) é recusado antes de
  ler o corpo.
- **Responde antes de processar.** O hook HTTP é síncrono, e o Claude espera a
  resposta. O corpo é validado e enfileirado, e a resposta `{}` ("sem
  decisão") sai na hora. Uma fila de um consumidor só aplica os avisos na
  ordem de chegada: "pergunta" e "voltou a trabalhar" não trocam de lugar.
- **Porta fixa** (`Application:AgentEventsPort`, 47831). Um Claude aberto
  guarda a URL até sair, e reabrir o app na mesma porta faz os que ficaram
  abertos voltarem a ser ouvidos. Se a porta estiver ocupada, vale qualquer
  livre para as sessões novas.
- Sem porta, o agente abre sem acompanhamento. Acompanhar é um extra e nunca
  impede de abrir.

**Avisar só na mudança, e não a quem já está olhando.** `RecordActivity`
devolve `true` só quando a atividade **muda**: o mesmo aviso repetido não
avisa duas vezes, e só a mudança é gravada (um `PostToolUse` por ferramenta
não vira uma escrita no banco por ferramenta). Se o terminal do agente é a
janela em primeiro plano (`ITerminalWindowManager.IsInForegroundAsync`, pela
mesma janela que o foco usa), o usuário está conversando com ele. Aí o estado
muda, mas nenhum aviso aparece por cima da conversa. Voltou a trabalhar ou o
processo saiu: o aviso sai da tela.

**O terminal pisca na barra de tarefas.** Junto com o aviso, a janela do
terminal do agente pisca (`FlashWindowEx` com `FLASHW_ALL | FLASHW_TIMERNOFG`,
via `ITerminalWindowManager.FlashAsync`, achada pela mesma janela do foco). O
aviso do canto pode ser dispensado ou nem caber na pilha; o botão laranja na
barra continua apontando onde está a pendência. Quem para de piscar é o
próprio Windows, quando o terminal vem para a frente, sem o app vigiar o foco.
Se o agente voltar a trabalhar antes, `StopFlashingAsync` (`FLASHW_STOP`)
apaga. Com várias abas na mesma janela do Windows Terminal, pisca a janela, não
a aba.

**O aviso é o do lembrete, com outra cara.** O `AlertPresenter` também
implementa `IAgentAttentionPresenter`. É a mesma pilha no mesmo canto, na tela
do painel, porque dois apresentadores empilhariam janelas uma sobre a outra.
A `AgentAlertWindow` nunca rouba o foco. Mostra a tarefa, repositório ·
branch, a pergunta ou o começo da resposta, e os botões "Abrir terminal"
(`FocusAgentSession`, o mesmo do selo) e "Dispensar". Há um aviso por sessão,
atualizado no lugar.

**Tela.**

- O selo da linha vira "⚠ Claude Code · aguardando você", "✓ Claude Code ·
  revisar" ou "⚠ Claude Code · erro", na cor de atenção. Com vários agentes,
  mostra o mais urgente (pergunta > erro > revisão > trabalhando), e o balão
  e o menu dizem o de cada repositório.
- Com pendência, a linha ganha uma moldura âmbar que respira (opacidade, 1,4 s)
  até o usuário clicar no selo; depois fica parada e fraca enquanto a
  pendência durar. "Já vista" é a chave (tarefa, ambiente, atividade,
  `ActivityChangedAt`), guardada em memória no `TodayViewModel`: o refresh não
  reacende a moldura, e uma pergunta nova do mesmo agente — outro horário —
  volta a pulsar. Reabrir o app volta a pulsar tudo, de propósito.
- O status do card diz a atividade, com "Às 18:55 · <pergunta>" embaixo e se a
  sessão avisa o app.
- A caixa "Avisar quando o agente precisar de mim ou terminar" fica junto dos
  parâmetros e segue o ADR-033: nasce ligada, e o valor usado no "Iniciar"
  vira o padrão (`AgentSettings.MonitorActivity`). A coluna `Arguments` ficou
  anulável: a linha pode existir só pelo acompanhamento, e os parâmetros
  continuam "nunca salvos".

**Persistência.** A migration `AgentActivity` acrescenta a `AgentSessions`
`HookTokenHash`, `ExternalSessionId` (o `session_id` do Claude, o último,
porque muda com `/clear`), `Activity`, `ActivityMessage` e
`ActivityChangedAt`, e a `AgentSettings`, `MonitorActivity`. Não há tabela de
eventos. O estado fica na sessão, e cada evento vai para o log
(`AgentEventReceived`, `AgentActivityChanged`, `AgentEventRejected`).

**Limites aceitos:**

- com o app fechado, os avisos se perdem (conexão recusada, o hook falha sem
  travar o Claude). Ao reabrir, a atividade mostrada é a última recebida, com
  o horário;
- sessões abertas antes desta versão, ou com o acompanhamento desligado, não
  avisam;
- com vários consoles como abas de uma janela do Windows Terminal, "está
  olhando" vale para a janela, e não para a aba (a limitação do ADR-030);
- as variáveis de ambiente, segredo incluído, são herdadas pelos comandos que
  o Claude roda. É a mesma fronteira de confiança: eles rodam como o usuário;
- conferido com o Claude Code real (`claude -p`): cabeçalhos interpolados,
  `Content-Length`, `Host` do loopback, `UserPromptSubmit`, `PostToolUse`,
  `Stop` com `last_assistant_message`, `SessionEnd` e os hooks do usuário
  rodando junto. `PreToolUse` de `AskUserQuestion`/`ExitPlanMode` e
  `Notification` só acontecem no modo interativo. Eles seguem o formato
  documentado e têm teste de tradução, mas ainda não foram vistos chegando de
  um Claude real.
