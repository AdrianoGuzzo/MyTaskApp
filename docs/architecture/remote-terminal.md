# Terminal do agente e terminal remoto do Claude Code

O mesmo Claude Code interativo, num terminal de verdade na máquina de
desenvolvimento, visto e controlado de outra máquina da conta, sem abrir uma
segunda conversa. As decisões estão no
[ADR-098](decisions/098-terminal-do-agente-em-pseudoterminal.md) (o
pseudoterminal no agente e as vistas locais) e no
[ADR-099](decisions/099-terminal-remoto-do-claude.md) (o relay e a entrada
assinada). O uso está em [`../remote.md`](../remote.md#o-terminal-do-claude) e
em [`../features/claude-code.md`](../features/claude-code.md).

## 1. Diagnóstico (antes desta mudança)

Havia dois caminhos que não se encontravam:

- **O terminal do card** (ADR-030, ADR-037). O desktop fazia `Process.Start`
  do `claude` num console novo (`WindowsTerminalLauncher`; no Linux,
  `LinuxTerminalLauncher`). Nada redirecionado, sem pseudoterminal. O app só
  sabia do estado pelos hooks HTTP, que chegam ao `AgentEventListener` do
  desktop. Ninguém lia a saída, e a sessão não aparecia de longe.
- **A sessão gerenciada** (ADR-089, ADR-091, ADR-095).
  `claude -p --restricted --output-format stream-json`, um processo por prompt,
  perfil fechado e aprovação por `--permission-prompt-tool`. É outra conversa:
  sem TUI, sem skills, sem a configuração do projeto e sem hooks.

| # | Pergunta | Resposta no código de antes |
|---|---|---|
| 1 | Onde o prompt remoto chega | `POST /api/v1/remote/operations` → `RemoteOperationUseCases.CreateAsync` → a campainha `OperationAvailable` do hub → o agente puxa por `RemoteOperationDispatcher.PollAsync` |
| 2 | Como vira execução | `AdmitRemoteOperationHandler` → `AgentStartExecutor`/`AgentPromptExecutor` → `HeadlessConversations` (a fila) → `HeadlessPromptRunner` |
| 3 | Interativo ou headless | de longe, só headless; o interativo era só local |
| 4 | Processo e diretório | `HeadlessAgentRunner`, com stdin e stdout redirecionados e a pasta do worktree; o local, `Process.Start` num console novo |
| 5 | Terminal real | no local, sim, mas sem a saída; de longe, não |
| 6 | Saída | no local, nenhuma; de longe, `ClaudeStreamJsonReader` → eventos → diário → REST → texto plano |
| 7 | Perguntas e aprovações | no local, o diálogo do próprio Claude, visto pelos hooks; de longe, o MCP de aprovação e a `ApprovalRequest` assinada (ADR-093) |
| 8 | Estado | no local, `AgentSession.Activity` pelos hooks; de longe, `RemoteSessionPublisher` → `PUT /agent/sessions` |
| 9 | O controlador cai | nada muda: os eventos ficam no servidor, e a aprovação vence em 10 min (nega) |

**Limitações de arquitetura:** não havia pseudoterminal nem emulador VT; os
hooks moravam no desktop; o hub só tocava a campainha; o catálogo não aceita
texto de shell; a sessão gerenciada é outra conversa.

**Limitações de implementação:** o interativo não passava `--session-id`; o
cancelamento não é assinado (R-08); a documentação dizia que o Linux não abre
terminal, mas o `LinuxTerminalLauncher` existia.

## 2. Arquitetura

O agente (`MyTaskApp.Agent`) passa a ser o dono do processo e de um
pseudoterminal real. A janela nativa, a vista dentro do app e o controlador de
longe são clientes da mesma sessão.

```
claude (interativo) ── ConPTY / openpty ── TerminalSessionHost (no agente)
                                             ├─ VtScreen + histórico (retrato e resync)
                                             ├─ hooks HTTP → listener do agente → estado e diálogo
                                             ├─ pipe local ─┬─ MyTaskApp.TerminalAttach (janela nativa)
                                             │              └─ TerminalView no desktop (dentro do app)
                                             └─ RemoteTerminalBridge ─ hub SignalR (relay, sem gravar)
                                                                         └─ RemoteTerminalChannel (controlador)
```

O caminho sem terminal (headless) e o terminal direto continuam como eram.

### Peças

| Onde | Peça | Papel |
|---|---|---|
| `Application/Terminals` | `TerminalSessionHost`, `VtScreen`, `TerminalSessions` | a sessão, a tela, a vez de digitar (lease), os clientes e o retrato |
| `Infrastructure/Terminals` | `ConPtyTerminal`, `UnixPtyTerminal` | o pseudoterminal do sistema, com Job (Windows) ou grupo de processos (Linux) |
| `Remote.Contracts/Terminals.cs` | `RemoteTerminalInput`, `RemoteTerminalFrame`, `RemoteTerminalState`, `RemoteTerminalSessionReport` | o fio, estrito (`UnmappedMemberHandling.Disallow`) |
| `Server.Application/RemoteTerminals.cs` | `RemoteTerminalUseCases`, `RemoteTerminalRelay`, `RemoteTerminalDirectory` | autoriza, repassa com fila limitada, guarda a lista só em memória |
| `Server.Api/RemoteHub.cs` | `TerminalSubscribe`, `TerminalSend`, `TerminalPublish`, `TerminalClose`, `TerminalHubFilter` | os métodos do hub e a cota por conexão |
| `Application/Remote/RemoteTerminals.cs` | `RemoteTerminalBridge`, `RemoteTerminalReporter`, executores `terminal.*` | o lado da máquina executora |
| `Application/Remote/RemoteTerminalInputVerifier.cs` | `RemoteTerminalInputVerifier` | confere cada lote assinado |
| `Application/Remote/RemoteTerminalControl.cs` | `RemoteTerminalConnector`, `RemoteTerminalChannel`, `ListRemoteTerminalsHandler` | o lado de quem controla |
| `Infrastructure/Remote` | `SignalRRemoteHub` (o agente), `SignalRRemoteTerminalWires` (uma conexão por vista) | os clientes do hub |
| `Application/Terminals` | `TerminalSessionClient`, `TerminalKeyBytes`, `TerminalColors` | o miolo de qualquer vista (ADR-101): a bomba do canal, a vez, a lacuna, as teclas xterm e a paleta |
| `Desktop` | `TerminalSessionViewModel`, `TerminalView`, `RemoteTerminalsViewModel`, `RemoteTerminalWindows` | a mesma vista para o local e o remoto; a lista de todas as máquinas em *Conversas do Claude* |
| `Mobile.Core` / `Mobile` | `RemoteTerminalViewModel`, `RemoteTerminalPage`, `TerminalDrawable` | o celular segue e controla (ADR-101): a tela num `GraphicsView`, a barra de teclas e o campo de prompt |

## 3. Fluxo de ponta a ponta

1. **Na executora**, a pessoa liga "Aceitar ser controlado" e "Aceitar o
   terminal remoto" (só nesta máquina, sem sincronizar). O registro passa a
   anunciar `terminal.start`, `terminal.attach` e `terminal.stop` nas
   capacidades, se o agente tiver pseudoterminal.
2. **A sessão abre** pelo card ("Onde abrir": terminal do agente) ou de longe,
   por `terminal.start`. De longe, só os ids vão: os parâmetros, o modelo e o
   acompanhamento são os salvos na executora, e o texto inicial não roda.
3. **O agente relata** as sessões (`PUT /api/v1/remote/agent/terminals`) a
   cada volta e logo depois de uma mudança. O servidor guarda em memória e
   avisa a conta (`TerminalsChanged`).
4. **O controlador lista** (`GET /api/v1/remote/terminals`) e, em "Seguir",
   abre uma conexão do hub só desta vista e pede `terminal.attach` com uma
   chave P-256 nova. O id da operação é o id da conexão. Ele assina
   (`TerminalSubscribe`) antes de o agente aceitar, para não perder o começo.
5. **O agente admite** o pedido como qualquer outro (assinatura do controlador
   pareado, alvo, conta, prazo). A ponte liga a conexão como cliente
   `Remote` da sessão. A operação termina na hora como `Succeeded` e não ocupa
   vaga.
6. **A saída** sai do host como no canal local: o retrato primeiro (o fluxo VT
   da tela, com o histórico) e depois os bytes, com deslocamento. A ponte junta
   o que chega em 15 ms (até 256 KiB) e corta em quadros de até 32 KiB. O
   retrato vai sozinho, e os pedaços dele levam o mesmo deslocamento. O
   servidor repassa para a fila da conexão (1 MiB). Se a fila estourar, ele
   descarta, manda `TerminalGap` e só volta a mandar saída a partir do próximo
   retrato, que o controlador pede.
7. **O teclado e as ações** saem do controlador como lotes JWS
   `mytaskapp-terminal-input+jws`, assinados pela chave da conexão, numerados
   e com hora. O servidor só confere que quem manda é quem pediu a conexão. O
   agente confere a assinatura, a conexão, a máquina, a sessão, a sequência
   estritamente crescente e o horário (2 min). Também confere, de novo, as duas
   chaves e o pareamento, com cache de 1 s: desligar no meio recusa o próximo
   lote e encerra a conexão.
8. **A vez de digitar.** De longe, ela é pedida ("Assumir o controle"). Quem
   digita na máquina toma de volta, e a vez vence após 10 min sem uso. Prompt,
   escolha e interromper pedem o acompanhamento pelos hooks e respeitam quem
   tem a vez.
9. **Sair** é fechar a conexão do hub. O servidor avisa o agente
   (`TerminalDetached`), que solta o cliente, e o Claude segue rodando.
   Reconectar é outro `terminal.attach`.
10. **Encerrar** é `terminal.stop`. `graceful` fecha o terminal (o Claude
    recebe o fim e salva a conversa); `force` termina o Job ou o grupo da
    sessão, e só ele. **Interromper** é o Esc, dentro da sessão.

## 4. Configuração

| | Windows | Linux |
|---|---|---|
| Pseudoterminal | ConPTY (`CreatePseudoConsole`), Windows 10 1809 ou mais novo | `openpty` + `posix_spawn` com `POSIX_SPAWN_SETSID` |
| Árvore da sessão | Job Object por sessão | grupo de processos: HUP, TERM e KILL |
| Janela nativa | `MyTaskApp.TerminalAttach.exe`, aberto pelo Windows Terminal ou pelo console | `MyTaskApp.TerminalAttach`, aberto pelo emulador de terminal achado (ver `LinuxTerminalLauncher`) |
| Agente | chave `Run` do usuário | unit `mytaskapp-agent.service` do `systemd --user` |
| Ambiente | `TERM=xterm-256color` | `TERM=xterm-256color`; sob o `systemd --user`, o `PATH` precisa achar o `claude` |

O macOS não tem o pseudoterminal do agente: "Onde abrir" fica no terminal do
sistema. No servidor, nada a configurar além de atualizar: as mensagens do hub
têm teto de 64 KiB (`MaximumReceiveMessageSize`) e 200 chamadas por segundo
por conexão (`TerminalHubFilter`). O relay supõe **uma instância** do servidor.

## 5. Segurança

O teclado de longe vale um shell na executora: o modo `!` do Claude roda
qualquer comando. Por isso:

- **Começa desligado e é por máquina.** "Aceitar o terminal remoto" exige
  "Aceitar ser controlado", pede confirmação e não sincroniza.
- **Só controlador pareado.** O `terminal.attach` é uma operação assinada como
  as outras (ADR-087); sem pareamento, o agente recusa.
- **A chave da conexão** é nova a cada conexão, mora só na memória do
  controlador e só assina lotes daquela conexão, máquina e sessão. Um lote
  repetido, fora de ordem, velho ou de outra chave é recusado.
- **O servidor não digita.** Ele não tem a chave da conexão, e o agente só
  aceita lote assinado por ela.
- **Revogar vale na hora.** Desligar a chave, desligar o controle ou deixar de
  confiar no controlador recusa o próximo lote e encerra a conexão; a sessão
  continua.
- **As aprovações do Claude ficam.** O agente nunca muda os argumentos para
  facilitar o controle. Uma sessão aberta sem as aprovações
  (`--dangerously-skip-permissions`, `bypassPermissions`) pode ser seguida,
  mas o controlador mostra o aviso e pergunta antes de assumir.
- **Nada gravado no servidor.** A saída passa pela memória do relay, com fila
  limitada, e o log tem só o tipo da mensagem. A lista de sessões leva ids e
  estado, sem a pasta e com os textos mascarados.
- **Encerrar só a sessão.** O `force` termina o Job ou o grupo da sessão;
  nenhum outro processo da máquina é tocado.
- **Limites explícitos.** Histórico do retrato de longe: 300 linhas. Fila por
  conexão: 1 MiB. Quadro: 32 KiB. Teclado por lote: 16 KiB. Até 8 conexões de
  longe por agente. Até 16 sessões por relato.

As ameaças e os riscos residuais estão no
[modelo de ameaças](remote-security-threat-model.md) (A-26 a A-30, R-12 a
R-14).

## 6. Limitações

- **A sessão vive com o agente.** Parar o agente encerra o Claude (o Job).
  Depois, "Retomar" abre outra sessão do mesmo histórico, por `--resume`.
- **Uma instância do servidor.** O relay não tem backplane.
- **O celular entrou depois (ADR-101).** A biometria é pedida uma vez, no
  attach. Em segundo plano a ligação cai, e voltar liga de novo. A letra
  encolhe para caber a largura de lá. Ainda não há seleção de texto nem aviso
  com o app fechado no iOS.
- **A saída passa em claro pelo servidor do usuário** (TLS, sem gravar). Cifra
  ponta a ponta fica como evolução.
- **As ações estruturadas dependem dos hooks.** As teclas de cada diálogo
  foram conferidas com o Claude de mentira (`tests/MyTaskApp.FakeClaude`). Com
  o Claude de verdade, os testes opcionais conferem só o que não chama o
  modelo. Sem acompanhamento, só o teclado vale.
- **Uma sessão aberta no terminal do sistema** ("Onde abrir": terminal do
  sistema) não é acompanhável de longe.
