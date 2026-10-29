# ADR-098 — O Claude interativo roda num pseudoterminal hospedado pelo agente, e as telas são vistas da mesma sessão

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Parcial |
| **Categoria** | remoto |
| **Registrado** | 2026-10-29 |
| **Palavras-chave** | pseudoterminal, ConPTY, openpty, posix_spawn, VtScreen, TerminalSessionHost, TerminalSessionRegistry, AgentHosting, AgentSessionMode.Terminal, MyTaskApp.TerminalAttach, canal local do terminal, lease de entrada, PermissionRequest, --session-id |
| **Relações** | revisa: [ADR-030](030-sessoes-claude-code-por-tarefa.md), [ADR-037](037-acompanhar-claude-code-pelos-hooks.md), [ADR-085](085-agente-sem-tela-sobre-o-mesmo-sqlite.md); relacionado: [ADR-092](092-sessao-gerenciada-a-partir-do-desktop.md), [ADR-099](099-terminal-remoto-do-claude.md) |
| **Código** | `src/MyTaskApp.Application/Terminals/` (`VtScreen`, `TerminalSessionHost`, `TerminalSessionRegistry`, `LocalTerminalServer`, `LocalTerminalChannel`, `TerminalWire`), `src/MyTaskApp.Infrastructure/Terminals/` (`Windows/ConPtyTerminal`, `Linux/UnixPtyTerminal`, `TerminalPipe`), `src/MyTaskApp.TerminalAttach/`, `src/MyTaskApp.Application/Agents/AgentSessionHandlers.cs` (`AgentHosting`) |

**Contexto:** o "Abrir Claude Code" do card abre um console novo com
`Process.Start` (ADR-030). Ninguém lê a saída: o app só sabe o que os hooks
contam (ADR-037), e eles chegam à porta do desktop. A sessão gerenciada
(ADR-089, ADR-092) é outra conversa (`claude -p --restricted`, sem TUI, sem
skills, sem hooks), e não serve para acompanhar o Claude de verdade.

Para seguir e controlar de outra máquina a **mesma** sessão interativa, alguém
precisa ser dono do processo e do terminal dele, e continuar de pé com o
desktop fechado. Esse alguém já existe: o agente sem tela (ADR-085).

**Decisão:**

1. **O agente hospeda o Claude num pseudoterminal real.** No Windows é o
   ConPTY (`CreatePseudoConsole`, processo criado suspenso, posto num Job por
   sessão e só então solto). No Linux é `openpty` + `posix_spawn` com
   `POSIX_SPAWN_SETSID`, o escravo aberto como fd 0 (vira o terminal de
   controle), `TIOCSWINSZ` e `TERM=xterm-256color`. Sem pacote novo: P/Invoke,
   como o resto do repositório. O macOS fica sem suporte, informado na tela.
2. **Um emulador VT em C# (`VtScreen`)** mantém a tela e um histórico limitado.
   O retrato (`Snapshot`) é um fluxo VT que redesenha a tela do zero: é o que
   recebe quem chega no meio, quem perdeu saída, e a janela que reconecta.
3. **Uma sessão, vários clientes (`TerminalSessionHost`).** A janela nativa,
   a vista embutida no app e os controladores remotos leem a mesma saída.
   - **Filas limitadas:** cada cliente tem 1 MiB pendente. Estourou, a fila é
     descartada e a próxima leitura é o retrato. Nada cresce sem limite.
   - **A vez de digitar (lease):** o teclado local preempta o remoto e avisa.
     O remoto assume de forma explícita, e o lease remoto vence em 10 minutos
     parado. As respostas que o terminal manda sozinho (DA, CPR, foco, mouse)
     não contam como digitação (`TerminalReplies`).
   - **Tamanho:** quem dita é a janela nativa, se houver. Senão, o dono do
     lease, a vista embutida, e por fim o primeiro cliente.
   - **Ações com estado:**
     - **prompt:** colagem entre colchetes, mais Enter; só com o Claude parado
       e sem diálogo;
     - **escolha:** Enter permite e Esc nega, só no diálogo aberto agora; um
       id velho é recusado, e tecla digitada depois que o diálogo abriu
       também;
     - **interromper:** Esc, só trabalhando;
     - **encerrar:** fecha o terminal, como fechar a janela;
     - **forçar:** mata a árvore da sessão pelo Job (Windows) ou pelo grupo
       (Linux, HUP → TERM → KILL).

     Sem acompanhamento (hooks desligados), só vale o teclado cru, e a tela
     diz por quê.
4. **"Abrir Claude Code" tem modo (`AgentHosting`).**
   - **Direct** é o padrão: o terminal direto de hoje, sem mudança.
   - **AgentWindow** é o terminal do agente numa janela nativa.
   - **AgentEmbedded** é o terminal do agente dentro do app.

   Os dois modos do agente rodam o **mesmo** `StartAgentSessionHandler`
   dentro do agente: pelo canal só vão ids e escolhas, e a linha de comando
   sai do banco. Esses modos usam `AgentSessionMode.Terminal`, com
   `--session-id` gerado (ou `--resume`, só por pedido explícito), e "Retomar"
   não muda os argumentos de ninguém.
5. **Os hooks dessas sessões vão ao agente.** Ele abre o próprio listener
   numa porta livre; a 47831 continua do desktop, com as sessões diretas. Cada
   lado ignora a sessão do outro (`AgentEventWrongListener`). O arquivo de
   hooks do terminal (`claude-code-hooks-terminal.json`) observa também o
   `PermissionRequest`, que traz a ferramenta e o comando: é dele que sai o
   texto do diálogo. Ele só observa (`{}`): quem aprova continua sendo a
   pessoa, no diálogo do Claude.
6. **O canal local do terminal** é um named pipe do usuário
   (`CurrentUserOnly`; no Linux, Unix socket com a mesma restrição), com o
   nome tirado da pasta do banco. Ele é separado do canal de avisos (T-11).
   - **Quadros:** 1 byte de tipo, 4 de tamanho e no máximo 256 KiB; quadro
     maior fecha a conexão sem alocar. O retrato vai em pedaços de 64 KiB.
   - **O que pode:** abrir uma sessão (`start`) e seguir uma (`attach`).
     Executa, ao contrário do canal de avisos, porque quem conecta é um
     processo do mesmo usuário, que já poderia abrir o Claude sozinho.
7. **A janela nativa é um programa à parte (`MyTaskApp.TerminalAttach`).**
   O agente é `WinExe` e tem instância única, então não pode ser a janela.
   O programa põe o console em modo cru e segue a sessão. **Fechar a janela
   desliga a vista; a sessão continua no agente.**
8. **Atenção:** o agente não tem tela. Ele grava a atividade e manda pelo
   canal de avisos `agentAttention` com o id da sessão. O desktop lê o estado
   no banco e chama a pessoa com som, aviso e a janela da vista piscando,
   como no terminal direto.

**Por quê:** só o dono do pseudoterminal tem a saída e pode escrever no mesmo
processo, e o agente é o processo que fica de pé sem o desktop. Manter o
terminal direto como padrão não tira nada de quem usa hoje, e um caso de uso
só para os três modos evita um segundo caminho de início.

## Alternativas

- **Ler a saída do console que o desktop abre.** O Windows não dá a saída de
  um console alheio, e no Linux o emulador é de outro processo. Seria raspar
  tela.
- **Usar a sessão gerenciada (`-p`) para tudo.** É outra conversa, sem TUI e
  com perfil fechado. A pessoa perde o Claude que conhece.
- **Hospedar no desktop.** A sessão morreria ao fechar o app, e o remoto
  depende do agente de qualquer forma.
- **Uma biblioteca de PTY ou de emulador (Pty.Net, XtermSharp).** Pty.Net não
  tem manutenção, e as duas trazem mais do que se usa. Os P/Invoke são poucos,
  e o emulador foi testado contra a saída real gravada do Claude.
- **Abrir a janela nativa pelo próprio agente.** Ele não tem console, e uma
  segunda instância dele sai na hora (mutex).

## Consequências

- **A sessão vive enquanto o agente vive.** O Job (Windows) e o grupo (Linux)
  levam a árvore junto quando o agente cai. Volta-se pela mesma conversa com
  "Retomar" (`--resume`).
- Uma sessão aberta pelo terminal direto não vira acompanhável: o modo é
  escolhido ao abrir.
- O mapeamento das teclas dos diálogos (Enter, Esc) foi conferido no Claude
  de mentira dos testes. Na versão do Claude instalada, ele é conferido pelos
  testes opcionais (`MYTASKAPP_TEST_CLAUDE=1`). Se não se sustentar, a ação
  cai para "responda no terminal".
- Até 8 sessões por agente, e a tela final fica 10 minutos depois da saída.
- O canal local é um canal de execução para processos do mesmo usuário, e
  entra no modelo de ameaças.

## Fora de escopo, de propósito

- O macOS: sem PTY nesta versão.
- Tornar acompanhável uma sessão já aberta no terminal direto.
- Mudar o perfil de permissões do Claude para facilitar o controle: os
  argumentos e os settings da pessoa são usados como estão.
