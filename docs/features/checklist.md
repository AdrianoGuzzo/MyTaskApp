# Checklist do dia

| | |
|---|---|
| **Status** | Implementado (Windows e Linux) |
| **Decisões** | [ADR-001](../architecture/decisions/001-tarefa-serie-e-ocorrencia.md) série × ocorrência · [ADR-010](../architecture/decisions/010-classificacao-hoje-funcao-pura.md) classificação de "Hoje" · [ADR-013](../architecture/decisions/013-captura-rapida-uma-linha-uma-tarefa.md) captura rápida · [ADR-022](../architecture/decisions/022-ordem-manual-por-arrasto.md) ordem manual |
| **Código** | `src/MyTaskApp.Application/Tasks/QuickCapture.cs`, `src/MyTaskApp.Application/Planning/`, `src/MyTaskApp.Domain/Planning/TodayClassifier.cs`, `src/MyTaskApp.Desktop/ViewModels/TodayViewModel.cs`, `src/MyTaskApp.Desktop/Views/TodayView.axaml`, `src/MyTaskApp.Desktop/Interactions/ReorderDrag.cs` |
| **Testes** | `tests/MyTaskApp.Application.Tests/Tasks/QuickCaptureHandlerTests.cs`, `tests/MyTaskApp.Domain.Tests/Planning/TodayClassifierTests.cs`, `tests/MyTaskApp.Desktop.Tests/ViewModels/TodayCaptureTests.cs` |

## Comportamento

- **Captura rápida.** A tela abre com uma caixa já focada. Cada linha vira uma
  tarefa para hoje, sem horário. `Enter` registra a lista inteira, `Shift+Enter`
  quebra a linha. A captura é atômica: ou entra tudo, ou nada (até 100 linhas).
- **Seções do dia.** `ATRASADAS`, `AGORA`, `HOJE`, `PRAZOS`, `SEM HORÁRIO` e
  `CONCLUÍDAS`. A janela "agora" vai de 15 min antes a 60 min depois do horário,
  e é configurável (`Application:NowWindow*`, [configuração](../configuration.md)).
  O quadro se atualiza sozinho a cada minuto. Com a sincronização ligada, o que
  chega de outro computador aparece no topo, em `CHEGARAM`
  ([sincronização](../sync.md#onde-cada-tarefa-aparece)).
- **Ordem manual.** Arraste as linhas para reordenar dentro da seção.
- **Concluir e reabrir** pelo checkbox. O título é editado na janela da tarefa.
  A prioridade aparece na linha, mas o desktop não tem controle para editá-la
  (veja abaixo).

## Divergências e lacunas (conferido em 2026-10-24)

- O README dizia "título e prioridade são editados na janela da tarefa". Só o
  título tem controle (`TaskNotesWindow.axaml`); `TaskNotesViewModel` lê e
  devolve a prioridade existente ao salvar, sem editá-la. A prioridade pode ser
  alterada pelo MCP (`task_update`) e pelo celular.
- O ADR-007 (Inbox como estado, `TriagedAt`) não está implementado como escrito:
  não existe `TriagedAt`; a tarefa sem data cumpre esse papel.
