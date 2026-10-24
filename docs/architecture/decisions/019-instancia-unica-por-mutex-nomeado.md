# ADR-019 — Instância única, e o clique no atalho que traz a janela de volta

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | distribuicao |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | SingleInstance, Mutex, EventWaitHandle, instância única, AppMutex, CheckForMutexes, Reveal |
| **Relações** | revisa: [ADR-016](016-bandeja-e-fechar-para-esconder.md); revisto por: [ADR-023](023-iniciar-com-windows-chave-run.md), [ADR-049](049-instalador-fecha-o-app-aberto.md); relacionado: [ADR-021](021-agendador-de-manutencao-do-ciclo-de-vida.md) |
| **Código** | `src/MyTaskApp.Desktop/Composition/SingleInstance.cs`, `src/MyTaskApp.Desktop/Program.cs`, `installer/windows/MyTaskApp.iss`, `tests/MyTaskApp.Packaging.Tests/WindowsInstallerContractTests.cs` |

**Decisão:** `Program.Main` adquire um `Mutex` nomeado antes de qualquer outra
coisa. O segundo lançamento **não abre nada**: sinaliza o primeiro e encerra
com 0.

**Por que agora:** o ADR-016 registrou isto como "fora de escopo — um `Mutex`
nomeado no `Program.Main` é a correção barata no dia em que isso morder". O
instalador é esse dia. Um atalho no Menu Iniciar torna relançar trivial, e a
bandeja torna relançar **provável**: a janela está escondida, o usuário acha que
fechou o app. Dois processos no mesmo SQLite significam aviso dobrado,
`SQLITE_BUSY` e dois `widget.json` disputando a mesma posição.

**O nome do mutex é contrato com o instalador.** `SingleInstance.MutexName` é o
mesmo valor que o `.iss` usa em `AppMutex`, que é como o Inno descobre que o app
está aberto antes de trocar binários — detecção de arquivo em uso não basta,
porque o processo pode estar ocioso na bandeja. Há teste de packaging comparando
os dois lados: renomear um sem o outro quebra a build, não a atualização de
alguém.

> **Revisto pelo ADR-049.** O `.iss` não usa mais `AppMutex`: o instalador
> fecha o app em vez de pedir que o usuário o feche. O mutex continua contrato
> (`CheckForMutexes`), como detecção de reserva.

**Encerrar calado seria pior do que não fazer nada.** Como o app vive na
bandeja, o segundo lançamento que apenas morresse deixaria o usuário clicando no
atalho sem ver resposta. Então o não-dono sinaliza um `EventWaitHandle` nomeado
e o dono revela a janela pelo `Reveal` que já existia em `App.axaml.cs`.

**Evento nomeado é API só do Windows** — no Linux e no macOS ele lança
`PlatformNotSupportedException`, então é criado sob `OperatingSystem.IsWindows()`
e lá o mutex sozinho resolve o que importa: o segundo processo encerra em vez de
duplicar o agendador. O `Mutex` nomeado, esse, funciona nos três.

**Detalhes que são decisão, não acaso:**
- o portão vem **antes** do logger: dois processos abrindo o mesmo arquivo de log
  do dia disputariam a trava exclusiva do sink, e o segundo nem precisa de log —
  ele só sinaliza e sai;
- o listener é `Thread` de fundo, não `Task`: a espera é bloqueante e dura toda a
  vida do app, e prender um thread do pool nisso seria pior;
- `ReleaseMutex` só no dono, dentro de `try`: soltar mutex que não se possui
  lança, e isso aconteceria justamente no caminho de encerramento;
- um mutex abandonado por processo que morreu volta a ser adquirível — uma queda
  não tranca o app para fora de si mesmo.

**Fora de escopo, e de propósito:** iniciar com o Windows. É a próxima
funcionalidade óbvia para um app de bandeja, seria uma linha no `[Registry]` do
instalador, e continua fora porque o ADR-016 a listou como fora — mudar isso
merece decisão própria, não uma carona no packaging.

> **Revisto pelo ADR-023**, que é a decisão própria que este parágrafo pediu. Não
> saiu por uma linha no `[Registry]`: o argumento `--startup`, o toggle no menu e
> a caixa que consulta o registro numa atualização vieram junto.
