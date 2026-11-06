# ADR-101 — O terminal do Claude em qualquer aparelho: o card abre no agente, a lista junta as máquinas e o celular segue e controla

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Parcial |
| **Categoria** | remoto |
| **Registrado** | 2026-11-04 |
| **Palavras-chave** | AgentHosting.AgentWindow padrão, HostingForAsync, RemoteTerminalAccepted, Usar o terminal do agente, ListRemoteTerminals sem máquina, Terminais do Claude, TerminalSessionClient, TerminalKeyBytes, TerminalColors, IRemoteTerminalConnector, RemoteTerminalViewModel, RemoteTerminalPage, TerminalDrawable, WentToBackground |
| **Relações** | revisa: [ADR-098](098-terminal-do-agente-em-pseudoterminal.md), [ADR-099](099-terminal-remoto-do-claude.md); relacionado: [ADR-087](087-pareamento-chave-por-dispositivo-operacao-assinada.md), [ADR-091](091-conversa-do-claude-como-sessao-no-servidor.md), [ADR-096](096-verificacao-periodica-no-celular.md); revisto por: [ADR-103](103-estado-do-claude-de-outras-maquinas-na-tarefa.md) |
| **Código** | `src/MyTaskApp.Application/Agents/IAgentSettingsStore.cs` (`HostingForAsync`), `src/MyTaskApp.Application/Remote/RemoteMachines.cs` (`UpdateRemoteSettingsHandler`), `src/MyTaskApp.Application/Remote/RemoteTerminalControl.cs` (`ListRemoteTerminals`, `IRemoteTerminalConnector`), `src/MyTaskApp.Application/Terminals/` (`TerminalSessionClient`, `TerminalKeyBytes`, `TerminalColors`), `src/MyTaskApp.Desktop/ViewModels/RemoteConversationsViewModel.cs`, `src/MyTaskApp.Mobile.Core/ViewModels/RemoteTerminalViewModel.cs`, `src/MyTaskApp.Mobile/Views/RemoteTerminalPage.xaml`, `TerminalDrawable.cs` |

**Estado da implementação (2026-11-04):** tudo abaixo está no código, com
testes do miolo e dos ViewModels do desktop e do celular. É parcial porque a
tela do celular foi compilada para Android, mas não foi conferida num aparelho
contra uma sessão de verdade, e o iOS só compila no CI do Mac.

**Contexto:** o espelho do terminal entre computadores (ADR-098, ADR-099)
funciona, mas a sessão nova de uma máquina não aparecia nas outras.

- **O padrão era o terminal direto.** "Onde abrir" começava no terminal do
  sistema, e o card grava a escolha a cada abertura. Na prática, a máquina
  ficava nele, e nada era relatado ao servidor.
- **Faltava uma lista das máquinas.** Quem controla só via os terminais de uma
  máquina dentro de *Controlar* dela.
- **O celular estava fora.** Ele ficou de fora da v1 pela biometria e pela tela
  pequena. Ele só via as conversas gerenciadas (`claude -p`, ADR-091), não o
  Claude interativo nem o estado dele.

**Decisão:**

1. **"Onde abrir" sem escolha é o terminal do agente, em janela, onde ele
   existe.** Sem pseudoterminal (macOS), continua o terminal direto. Quem já
   escolheu continua com a escolha. O comportamento de quem escolheu o
   terminal direto não muda.
2. **Ligar "Aceitar o terminal remoto" põe o "Abrir Claude Code" no terminal
   do agente.**
   - Isso vale só na passagem de desligado para ligado, e só para o terminal
     direto ou o nunca escolhido.
   - A confirmação da chave avisa.
   - Quem usa o terminal do agente dentro do app fica nele.
3. **O card avisa quando a sessão não vai aparecer de longe.** Com o terminal
   remoto aceito e "Onde abrir" no terminal do sistema, o card diz "Este
   terminal não aparece nos outros aparelhos" e oferece "Usar o terminal do
   agente". Nada é trocado sem a pessoa ver.
4. **Uma lista de todas as máquinas.**
   - **O handler:** `ListRemoteTerminals` sem máquina traz as sessões da conta,
     as rodando primeiro e as mais recentes na frente. O servidor já devolvia
     a conta inteira; só o filtro era do cliente.
   - **O desktop:** a janela *Conversas do Claude* ganha, em cima, os
     **Terminais do Claude** das outras máquinas, com Seguir.
   - **O celular:** a aba Remoto ganha a mesma lista, e a tela da máquina mostra
     os terminais dela.
5. **O celular segue e controla o terminal.**
   - **A ligação:** é o mesmo `terminal.attach` assinado, com a mesma chave
     efêmera por conexão. O destravamento é pedido uma vez, ao ligar. As
     teclas vão assinadas pela chave da conexão, que só existe na memória.
   - **O segundo plano:** ir para segundo plano solta a ligação, e a chave vai
     junto. Voltar liga de novo, com outra chave, e pede o destravamento se a
     janela de 30 s passou.
   - **A tela:** é a `VtScreen` desenhada num `GraphicsView`. A letra diminui
     para caber a largura de lá, e a pinça muda o tamanho. Arrastar rola o
     histórico.
   - **O teclado:** o do sistema digita direto. Uma barra traz Esc, Tab, ⇧Tab,
     as setas, Enter, Ctrl+C e `/`. Um campo de prompt manda o texto pronto,
     colado e com Enter, quando o Claude espera.
   - **As ações:** Assumir e Soltar o controle, Permitir e Negar, Interromper,
     Encerrar e Forçar o fim, com as mesmas regras de estado e de vez do
     ADR-098.
   - **O aviso com o app fechado (Android, ADR-096):** avisa uma vez quando um
     terminal passa a esperar alguém.
6. **Um miolo só para as vistas.** O que não é tela sai do desktop para a
   Application:
   - **`TerminalSessionClient`:** a bomba do canal, a tela, a lacuna, a vez e
     as ações;
   - **`TerminalKeyBytes`:** as tabelas xterm;
   - **`TerminalColors`:** a paleta.

   A janela do Avalonia e a tela do MAUI só desenham e traduzem teclas.
   `IRemoteTerminalConnector` é a porta da ligação de longe, para o celular
   ser testado sem o hub.

**Por quê:** a pessoa quer abrir o Claude pelo card numa máquina, ver na outra
e assumir dali. Isso só acontece se a sessão nascer no terminal do agente.
Mudar o padrão e avisar no card resolve sem trocar a escolha de ninguém sem
ela ver. No celular, o caminho assinado já existia. A biometria só é pedida no
attach, e a tela pequena se resolve com a letra que encolhe, a barra de teclas
e o campo de prompt. Um miolo só evita duas versões das regras de vez, lacuna e
diálogo.

## Alternativas

- **Migrar em silêncio quem tem o terminal direto salvo.** O card grava a
  escolha a cada abertura, e não dá para separar quem escolheu de quem só
  aceitou o padrão. O aviso no card trata os dois sem surpresa.
- **Tornar acompanhável o Claude aberto no terminal direto.** Continua fora,
  pelo mesmo motivo do ADR-098: ninguém tem a saída daquele console.
- **Um lançador para o `claude` digitado em qualquer terminal.** Seria uma
  sessão sem tarefa nem ambiente, e o `terminal.start` e o lease pedem os dois.
  Fica para quando houver pedido.
- **O celular só com a lista e o estado.** Seria seguro, mas a pessoa quer a
  mesma experiência do desktop.
- **Biometria a cada lote de teclas.** Inviável para digitar. A chave efêmera
  por conexão já limita o que o servidor pode fazer (A-27).
- **Manter a ligação em segundo plano.** O sistema congela o processo de
  qualquer jeito. Soltar já leva a chave da memória, e voltar religa.

## Consequências

- Quem abre pelo card numa máquina com pseudoterminal passa a rodar o Claude no
  agente: fechar a janela não encerra o Claude (ADR-098).
- O celular é mais um controlador do terminal. Um celular destravado na mão de
  outra pessoa, dentro dos 30 s, pode assumir. Isso já valia para as
  aprovações, e entra no modelo de ameaças (A-30).
- O tamanho que o celular pede só vale com a vez e sem a janela nativa de lá
  (ADR-098). Senão, a letra encolhe até caber, com mínimo de 6 pt.
- O desktop não chama a pessoa quando aparece um terminal novo em outra
  máquina: o desktop só ouve o hub com uma tela de controle aberta.

## Fora de escopo, de propósito

- O `claude` digitado num terminal fora do app.
- O macOS como máquina que hospeda (ADR-098).
- Push no iOS: o aviso com o app fechado continua só no Android (ADR-096).
- Seleção e cópia de texto na tela do celular.
