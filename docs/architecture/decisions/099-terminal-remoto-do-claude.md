# ADR-099 — O terminal do agente seguido e controlado de outra máquina, com entrada assinada

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Parcial |
| **Categoria** | remoto |
| **Registrado** | 2026-10-29 |
| **Palavras-chave** | terminal.start, terminal.attach, terminal.stop, relay do hub, TerminalSubscribe, TerminalPublish, entrada assinada, mytaskapp-terminal-input+jws, chave efêmera, Aceitar terminal remoto, AcceptTerminal, bypass com aviso, RemoteTerminalSessionReport |
| **Relações** | revisa: [ADR-088](088-catalogo-fechado-de-operacoes-remotas.md), [ADR-094](094-controladores-ouvem-o-hub.md); relacionado: [ADR-084](084-orquestracao-remota-coordenador-e-agente.md), [ADR-086](086-operacao-remota-registro-diario-e-signalr.md), [ADR-087](087-pareamento-chave-por-dispositivo-operacao-assinada.md), [ADR-089](089-claude-code-remoto-headless-com-aprovacao.md), [ADR-098](098-terminal-do-agente-em-pseudoterminal.md); revisto por: [ADR-101](101-terminal-do-claude-em-qualquer-aparelho.md), [ADR-103](103-estado-do-claude-de-outras-maquinas-na-tarefa.md) |
| **Código** | `src/MyTaskApp.Remote.Contracts/Terminals.cs`, `src/MyTaskApp.Server.Application/RemoteTerminals.cs`, `src/MyTaskApp.Server.Api/RemoteHub.cs` (relay e `TerminalHubFilter`), `src/MyTaskApp.Application/Remote/RemoteTerminals.cs` (ponte, relato, executores), `RemoteTerminalInputVerifier.cs`, `RemoteTerminalControl.cs` (o controlador), `src/MyTaskApp.Infrastructure/Remote/SignalRRemoteTerminalWires.cs`, `src/MyTaskApp.Desktop/ViewModels/RemoteTerminalsViewModel.cs` |

**Estado da implementação (2026-11-02):** os itens 1 a 7 estão no código, com
o teste de ponta a ponta contra o servidor de verdade
(`RemoteTerminalEndToEndTests`). Falta conferir com o Claude de verdade, logado,
o que depende de resposta do modelo (as teclas de cada diálogo e o Esc); os
testes opcionais (`ClaudeTerminalTests`) conferem só o que não chama o modelo.
Desenho e fluxo: [`../remote-terminal.md`](../remote-terminal.md).

**Contexto:** com o Claude interativo num pseudoterminal do agente (ADR-098),
falta levar a mesma sessão a outra máquina da conta. O caminho remoto que
existe é seguro mas fechado: o catálogo não aceita texto de shell (ADR-088), e
o hub só toca a campainha (ADR-086, ADR-094). Um terminal remoto dá, na
prática, um shell na máquina: o modo `!` do Claude executa qualquer comando.

**Decisão:**

1. **Opt-in na executora.** Uma chave nova, "Aceitar terminal remoto", começa
   desligada, vale só nessa máquina e não sincroniza. Ela exige "Aceitar ser
   controlada" ligado. Os tipos de terminal só aparecem nas capacidades da
   máquina quando ela tem pseudoterminal e a chave está ligada.
2. **Três tipos novos no catálogo**, assinados pelo controlador pareado, como
   os outros (ADR-087):
   - **`terminal.start`:** só o ambiente. Usa os `AgentSettings` salvos na
     executora, nunca texto de argumento vindo de longe.
   - **`terminal.attach`:** a sessão, o ambiente, a tarefa e uma chave P-256
     **efêmera** do controlador. Termina na hora (`Succeeded`), e o id da
     operação vira o id da conexão. Não ocupa vaga de operação.
   - **`terminal.stop`:** `graceful` (fechar) ou `force` (matar a árvore).
3. **Cada lote de entrada é um JWS** `typ: mytaskapp-terminal-input+jws`,
   assinado com a chave efêmera. O agente confere a conexão, o alvo, a
   sequência estritamente crescente e o horário. A cada lote ele confere de
   novo as duas chaves da executora e a confiança no controlador: desligar no
   meio recusa o próximo lote.
4. **O servidor só repassa.** Métodos novos no hub:
   - `TerminalSubscribe` e `TerminalSend`, para quem pediu a conexão;
   - `TerminalPublish` e `TerminalClose`, para a máquina alvo.

   Cada conexão é um grupo, com fila limitada por assinante (estourou, vai um
   aviso de lacuna e o cliente pede o retrato). Mensagem de no máximo 64 KiB,
   taxa por conexão, nada persistido e log só do tipo. Uma instância do
   servidor, sem backplane.
5. **A saída vai como no canal local:** deslocamento, bytes e `reset`. O
   retrato é o mesmo fluxo VT do `VtScreen`.
6. **Bypass com aviso.** O agente detecta
   `--dangerously-skip-permissions`, `--allow-dangerously-skip-permissions`,
   `--permission-mode bypassPermissions` e o `defaultMode` dos settings. A
   sessão é hospedada e controlada mesmo assim, e o controlador mostra o aviso.
   Os argumentos nunca são trocados.
7. **As ações estruturadas são as do ADR-098** (prompt, escolha, interromper,
   encerrar, forçar), com a mesma regra de estado e o mesmo lease. O remoto
   precisa assumir a vez para digitar.

**Por quê:** o caminho assinado e o catálogo continuam sendo a única porta de
execução de longe, e o terminal entra nele como um tipo a mais, com chave
própria. A chave efêmera por conexão evita pedir a assinatura do aparelho a
cada tecla, e o agente conferindo a cada lote faz a revogação valer na hora.

## Alternativas

- **Aceitar o teclado sem assinatura, só pelo hub autenticado.** O servidor
  passaria a poder digitar no shell da pessoa: ele é intermediário, não fonte
  de comandos (ADR-084).
- **Só ações estruturadas, sem teclado.** Seguro, mas não é o Claude da
  pessoa: menus, `/comandos` e edição de prompt ficariam de fora. Foi a opção
  recusada na conversa.
- **Recusar sessões com bypass.** Esconderia o terminal de quem já roda assim
  localmente. Avisar mostra o risco sem mudar o perfil escolhido.
- **Cifrar ponta a ponta já (ECDH + AES-GCM).** Fica como evolução: na v1 a
  saída passa em claro pelo servidor do próprio usuário (TLS), sem gravar.

## Consequências

- O modelo de ameaças ganha o teclado remoto (≈ shell), o bypass e a saída em
  claro no servidor.
- O celular fica de fora da v1: a assinatura pede biometria, e a tela é
  pequena para a TUI.
- Com o agente parado, o pedido espera na fila do servidor como os outros, e
  o controlador mostra "agente indisponível".

## Fora de escopo, de propósito

- Várias instâncias do servidor (backplane).
- Gravação ou replay da saída no servidor.
- Terminal remoto para sessões abertas no terminal direto.
