# ADR-030 — Sessões de agente de IA (Claude Code) por tarefa

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | agente-ia |
| **Registrado** | 2026-09-23, commit `825c4fc` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | AgentSession, IAgentCliProvider, ClaudeCodeCliProvider, WindowsTerminalLauncher, AgentSessionMonitor, AgentSessionReconciler, Iniciar Claude Code, PID |
| **Relações** | revisto por: [ADR-031](031-varios-ambientes-por-tarefa.md); relacionado: [ADR-015](015-agendador-com-timeprovider-createtimer.md), [ADR-021](021-agendador-de-manutencao-do-ciclo-de-vida.md), [ADR-027](027-aba-desenvolvimento-worktree-git.md), [ADR-033](033-parametros-do-agente-lembrados.md), [ADR-036](036-abrir-claude-code-menu-da-linha.md), [ADR-037](037-acompanhar-claude-code-pelos-hooks.md), [ADR-039](039-arroba-ambientes-e-arquivos-no-agente.md), [ADR-040](040-modelo-e-esforco-do-agente.md), [ADR-065](065-limpeza-de-worktrees-com-forca.md), [ADR-081](081-consulta-inteligente-somente-leitura.md) |
| **Código** | `src/MyTaskApp.Domain/Agents/AgentSession.cs`, `src/MyTaskApp.Application/Agents/AgentSessionMonitor.cs`, `src/MyTaskApp.Infrastructure/Agents/ClaudeCode/ClaudeCodeCliProvider.cs`, `src/MyTaskApp.Infrastructure/Terminals/Windows/WindowsTerminalWindowManager.cs` |

**Decisão:** com o worktree pronto (ADR-027), a aba Desenvolvimento ganha um
card do agente de IA. "Iniciar Claude Code" abre o `claude` num **terminal real
do sistema**, com o worktree como pasta de trabalho. O app grava uma
**sessão** (`AgentSessions`) que liga a tarefa ao processo: `Id`,
`TaskItemId`, `ProviderId`, `Command` (o executável, em caminho absoluto),
`WorkingDirectory`, `ProcessId`, `ProcessStartedAt`, `StartedAt`, `EndedAt`,
`Status` (`Starting` → `Running` → `Exited`, ou `Starting` → `Failed`) e
`FailureReason`. O card mostra PID, início e a pasta. "Abrir terminal do agente"
traz aquela janela para a frente. A linha da lista ganha um selo discreto,
"● Claude Code", enquanto a sessão está em execução. Um clique no selo faz o
mesmo que "Abrir terminal do agente", sem abrir a tarefa: é o mesmo caso de uso
(`FocusAgentSession`). Se o processo já acabou, o quadro é recarregado, e o selo
some com um aviso. O objetivo é um só: não se perder entre vários terminais
abertos.

**Só manual.** Nada abre sozinho ao fim de "Iniciar implementação": o botão
fica no card do estado Pronto. O app não captura, não lê e não escreve na
sessão. O usuário conversa com o Claude no terminal, como sempre.

**Um agente é uma porta, não o fluxo.** `IAgentCliProvider` (Application) diz
`Id`, `Name`, `Command`, como detectar (`DetectAsync`, que devolve caminho e
versão), como instalar por sistema (`InstallGuideFor`) e **o que** executar
(`CreateLaunch`). Hoje há um só, o `ClaudeCodeCliProvider` (Infrastructure).
Ele procura `claude.exe` e `claude.cmd` no PATH, relido do registro como o do
Git, e em `%USERPROFILE%\.local\bin` (instalador nativo) e `%APPDATA%\npm`. A
versão sai de `claude --version`, que responde e sai, sem sessão interativa.
Sem versão, ele continua "instalado". As instruções moram em
`WindowsInstallationGuide` e `LinuxInstallationGuide`, e em nenhum outro lugar.
Codex, Gemini e outros entram como mais um `IAgentCliProvider` registrado. O
catálogo (`IAgentCliProviders`) escolhe pelo id gravado na sessão, e nada de
"Claude" aparece no domínio nem nos casos de uso. A busca no PATH saiu do
`GitLocator` para o `ExecutableLocator`, compartilhado pelos dois.

**Por que abrir o programa direto, e não pelo `wt.exe`.** O `wt.exe` é um
lançador: entrega o pedido ao Windows Terminal e sai na hora. O PID dele morre
em milissegundos, e o shell de verdade nasce filho do `WindowsTerminal.exe`,
sem ligação que se possa seguir. O `WindowsTerminalLauncher` inicia o próprio
`claude` com `UseShellExecute = false`, `CreateNoWindow = false`, `ArgumentList`
com os parâmetros escolhidos (ADR-032) e `WorkingDirectory` = worktree. O MyTaskApp é um app gráfico, sem
console, então o Windows cria um **console novo** para o filho, hospedado pelo
**terminal padrão do usuário** (Windows Terminal, se estiver configurado como
padrão; senão o console clássico). O PID é o do próprio agente e vive exatamente
enquanto ele vive. Instalado pelo npm, o `claude.cmd` é aberto pelo Windows via
`cmd.exe`, e o PID é o desse `cmd`, que também vive enquanto o Claude vive. Por
isso o terminal **fecha junto** com o Claude (`/exit`): "Finalizado" quer dizer
o fim do Claude, e não o de um shell em volta. Sem `cmd /c <linha>`, sem
concatenação: executável e pasta precisam ser caminhos absolutos que existem, e
o launcher confere antes de iniciar.

**PID + horário de início.** O Windows reaproveita PIDs. A sessão guarda também
o `StartTime` do processo, e `IAgentProcessTracker.IsAlive(pid, início)` só
aceita o processo se o início bater (com folga de 1 s). PID igual com início
diferente é outro programa, e a sessão é dada como encerrada. Acesso negado
conta como "não é a sessão".

**Achar a janela pelo processo, nunca pelo título.** Um programa de console não
é dono da janela em que aparece. O `WindowsTerminalWindowManager` pergunta ao
próprio console: `AttachConsole(pid)` → `GetConsoleWindow()` → `FreeConsole()`,
num `lock`, porque o console é um por processo. No console clássico, essa já é a
janela visível. No Windows Terminal, é uma `PseudoConsoleWindow` cuja **dona** é
a janela do Terminal: `GetAncestor(GA_ROOTOWNER)` chega nela. Depois vêm
`ShowWindow(SW_RESTORE)`, se estiver minimizada, e `SetForegroundWindow`. Se o
Windows recusar o foco (o app não estava na frente), há um fallback com
`AttachThreadInput` à thread da janela em foco. A `Process.MainWindowHandle`
fica como último recurso. Verificado de ponta a ponta no Windows 11 com o
Windows Terminal como padrão. **Limitação:** se o Terminal juntar vários
consoles como abas de uma mesma janela, a janela vem para a frente, mas a aba
certa pode não ser selecionada.

**O banco não é a verdade; o processo é.** Toda leitura para a tela
(`GetTaskAgentSession`, `FocusAgentSession`) passa antes por
`AgentSessionReconciler.EndIfGone`: sessão ativa cujo processo não existe mais
vira `Exited` e é gravada antes de voltar. "Abrir terminal" nunca abre um agente
novo nem mira num PID reaproveitado. Uma sessão `Starting` sem PID só é dada
como perdida depois de 2 minutos, para a reconciliação não encerrar uma sessão
que outra operação está terminando de abrir.

**Monitor por evento, não por polling (`AgentSessionMonitor`).** Segue o molde
de ADR-015 e ADR-021. Singleton na Application, `IUseCaseRunner` para o escopo,
`TimeProvider.CreateTimer`, primeiro tique imediato. O tique roda
`ReconcileAgentSessions`: processo vivo volta a ser vigiado, processo sumido vira
`Exited`, e **nenhuma sessão é criada**. É assim que o app reencontra o Claude
depois de reaberto. Cada sessão viva tem um vigia `Process.Exited`
(`EnableRaisingEvents`, que funciona também para processos que o app não
iniciou), e o fim do Claude chega na hora. O timer é só rede de segurança:
`Application:AgentSessionReconcileSeconds`, padrão 60 s, com limites de 10 s a
1 h. Os casos de uso avisam o monitor por `IAgentSessionWatcher` (o mesmo
objeto). O `SessionsChanged(taskId)` chega de qualquer thread, e a `App` posta na
thread de UI, recarrega a lista e atualiza o card da janela aberta daquela
tarefa. A janela é avisada pela `App`, e não assinando o monitor: um singleton
segurando o evento de um ViewModel transitório o manteria vivo depois de a
janela fechar.

**Fechar o app não encerra o Claude.** Descartar o monitor só solta os vigias.
O processo continua no terminal, e a próxima abertura o reencontra pelo PID e
pelo início.

**Uma sessão ativa por tarefa** (por ambiente desde o ADR-031, com o índice
`IX_AgentSessions_TaskDevelopmentId_Active`). O caso de uso recusa a segunda ("use Abrir
terminal do agente"). Se a anterior já morreu, ela é encerrada e gravada
**antes** da nova ser inserida. Um índice único parcial
(`IX_AgentSessions_TaskItemId_Active`, `"Status" IN (1, 2)`) impede o banco de
aceitar duas. A sessão é gravada `Starting` **antes** de o terminal abrir: se o
app cair no meio, sobra uma sessão sem PID que a reconciliação encerra, e não
um terminal órfão sem registro. Falha ao abrir vira sessão `Failed`, com o
motivo, e nunca `Running`. Agente que sai na hora vira `Exited`. Agente não
instalado é recusado **sem** criar sessão, e o card troca o botão pelas
instruções. Remover o worktree com o agente aberto é recusado
(`RemoveWorktreeHandler`), porque o processo está com a pasta aberta.

**Camadas.**

- Domínio: `AgentSession` é um aggregate próprio, e não filho de `TaskItem`.
  Quem muda o estado é um processo de fora, e carregar a tarefa inteira para
  trocar um status seria desperdício. A FK para `Tasks` tem cascade: excluir a
  tarefa de vez leva o histórico.
- Application: portas `IAgentCliProvider`, `ITerminalLauncher`,
  `ITerminalWindowManager`, `IAgentProcessTracker` e `IAgentSessionRepository`,
  mais os casos de uso e o monitor.
- Infrastructure: o provider, o tracker (.NET puro e multiplataforma) e, só no
  Windows, `WindowsTerminalLauncher` e `WindowsTerminalWindowManager`
  (`[SupportedOSPlatform]`, `LibraryImport`). Em outros sistemas, o registro
  escolhe `UnsupportedTerminalLauncher` e `UnsupportedTerminalWindowManager`,
  que respondem "ainda não suportado". O Linux entra trocando essas duas
  classes.

**Limites aceitos:**

- só o Claude Code, e sem escolher agente pela UI;
- sem terminal embutido e sem ler o que se passa na sessão (o ADR-037
  acompanha a atividade pelos hooks, sem ler o terminal);
- sem "Parar" (não há status `Stopped`): quem encerra é o usuário, no terminal;
- a aba do Windows Terminal pode não ser a selecionada (ver acima);
- Linux e macOS: abstrações prontas, sem implementação de terminal.

## Divergências com o código (conferido em 2026-10-24)

- O texto cita "parâmetros escolhidos (ADR-032)"; o ADR certo é o [ADR-033](033-parametros-do-agente-lembrados.md) (o 032 é o corretor ortográfico).
