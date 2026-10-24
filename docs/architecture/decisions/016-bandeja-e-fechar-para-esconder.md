# ADR-016 — Bandeja, `OnExplicitShutdown` e fechar-para-esconder

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ui-desktop |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | bandeja, TrayIcon, OnExplicitShutdown, fechar para esconder, TrayIconUnavailable, ShutdownMode |
| **Relações** | revisto por: [ADR-019](019-instancia-unica-por-mutex-nomeado.md), [ADR-023](023-iniciar-com-windows-chave-run.md); relacionado: [ADR-044](044-versionamento-com-release-please.md), [ADR-048](048-hud-visivel-sem-atrapalhar.md), [ADR-049](049-instalador-fecha-o-app-aberto.md) |
| **Código** | `src/MyTaskApp.Desktop/App.axaml.cs`, `src/MyTaskApp.Desktop/Reminders/TrayIconHost.cs` |

**Decisão:** o app vive na bandeja do Windows. Fechar a janela a **esconde**;
sair mesmo é pelo menu da bandeja. `ShutdownMode` vira `OnExplicitShutdown`.

**Por quê:** "o sistema fica responsável por me lembrar" só é verdade se o
processo estiver vivo às 15:00. Com o padrão `OnLastWindowClose`, fechar a janela
mataria o app e todo lembrete pendente junto.

**A saída de emergência é parte da decisão:** se o `TrayIcon` falhar ao subir,
`TryInstall` devolve `false`, o log recebe `TrayIconUnavailable` e o
`ShutdownMode` **volta** para `OnMainWindowClose`. Sem isso um app sem bandeja
ficaria sem nenhuma forma de ser encerrado.

**Duas armadilhas da API, registradas porque falham em silêncio:** a coleção
`TrayIcons` precisa ficar em campo (coletada, o ícone some da bandeja sem erro
nenhum) e o `TrayIcon` precisa ser descartado na saída (senão fica ícone fantasma
até o usuário passar o mouse por cima).

**Limite aceito:** o Windows bloqueia roubo de foreground por processo em segundo
plano, então o último degrau da escada pode apenas piscar na barra de tarefas em
vez de trazer a janela à frente. Quem realmente chama atenção é a janela de
aviso; o degrau 5 é reforço, não garantia.

**Fora de escopo:** iniciar com o Windows, e instância única. Nada impede hoje um
segundo processo, e a bandeja torna relançar mais provável (a janela está
escondida, o usuário acha que fechou). Dois agendadores no mesmo SQLite significam
aviso dobrado e `SQLITE_BUSY`; um `Mutex` nomeado no `Program.Main` é a correção
barata no dia em que isso morder.

> **Revisto.** Instância única entrou no ADR-019; iniciar com o Windows, no
> ADR-023 — e foi justamente a premissa deste ADR ("o processo precisa estar
> vivo às 15:00") que acabou forçando a segunda.
