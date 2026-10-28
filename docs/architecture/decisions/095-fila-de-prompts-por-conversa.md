# ADR-095 — A instrução com o Claude respondendo espera a vez na fila da conversa

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | remoto |
| **Registrado** | 2026-10-28 |
| **Palavras-chave** | fila por conversa, HeadlessConversations.WaitAsync, MaxWaiting, processo vivo, stream-json, replay-user-messages |
| **Relações** | revisa: [ADR-089](089-claude-code-remoto-headless-com-aprovacao.md); relacionado: [ADR-091](091-conversa-do-claude-como-sessao-no-servidor.md) |
| **Código** | `src/MyTaskApp.Application/Remote/AgentSessionExecutors.cs` (`HeadlessConversations`, `AgentPromptExecutor`), `src/MyTaskApp.Application/Remote/RemoteSessions.cs` (`RemoteSessionTexts.CanPrompt`) |

**Contexto:** o Claude sem terminal roda um processo por prompt, com
`--resume` (ADR-089). Dois `--resume` na mesma conversa ao mesmo tempo misturam
as duas, então a fase 3 (E1) passou a recusar o segundo prompt: "O Claude ainda
está respondendo o prompt anterior." Quem acompanha do celular quer mandar a
próxima instrução sem esperar a resposta acabar.

A documentação oficial oferece outro caminho: um `claude -p` que fica aberto
lendo mensagens do stdin (`--input-format stream-json`) e devolve cada uma no
stdout (`--replay-user-messages`) como recibo de entrega.

**Decisão:**

1. **Uma fila por conversa no agente.** O prompt que chega com outro rodando
   espera a vez, na ordem em que chegou, sem tocar na conversa. A linha do
   tempo mostra "Na fila: o Claude termina o prompt anterior e responde este
   em seguida." Quando a vez chega, ele é conferido de novo, como qualquer
   prompt.
2. **Um esperando, no máximo** (`MaxWaiting`). O seguinte é recusado: "Já há
   um prompt esperando a vez nesta conversa." Cada prompt na fila ocupa uma
   das operações simultâneas do agente.
3. **Parar dispensa a fila**: o que esperava termina com "A conversa foi parada
   antes deste prompt." Cancelar o pedido o tira da fila.
4. As telas oferecem "Mais um prompt" também com o Claude respondendo
   (`CanPrompt`). Um agente antigo recusa como antes.
5. **O processo vivo fica avaliado, não adotado.** O teste opcional
   `LiveProcessClaudeTests` guarda o comportamento (duas mensagens no mesmo
   processo, cada uma com recibo, a mesma sessão). Ele não entra no caminho do
   agente.

**Por quê:** a fila entrega o que foi pedido (a instrução no meio chega depois
da resposta atual, na ordem) sem mudar o modelo de um processo por prompt, que
já tem prazo, teto de turnos e custo, parada e recuperação depois da queda. O
processo vivo pediria vigiar um processo ocioso por conversa, reaplicar os
tetos por mensagem e recuperar uma conversa no meio do stdin. E o recibo dele
só diz que o processo leu, não que o Claude respondeu.

## Alternativas

- **Continuar recusando.** Quem está longe teria de esperar e tentar de novo.
- **Fila sem teto.** Prompts empilhados ocupariam as operações simultâneas do
  agente e travariam os outros pedidos da máquina.
- **Processo vivo com `--input-format stream-json`.** Avaliado no item 5; fica
  como opção se a instrução precisar entrar *durante* a resposta, e não depois.

## Consequências

- A instrução mandada no meio é entregue depois da resposta atual, na ordem.
- Um prompt na fila ocupa uma operação simultânea (o padrão é 3).
- A fila vive na memória do agente: reiniciado, o pedido que esperava termina
  como interrompido, como qualquer operação que caiu com o agente.

## Fora de escopo, de propósito

- Mudar a ordem da fila, ou editar um prompt que espera.
- A instrução entrando no meio da resposta (pede o processo vivo).
