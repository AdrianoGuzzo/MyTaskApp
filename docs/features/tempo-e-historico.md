# Tempo trabalhado e histórico de 7 dias

| | |
|---|---|
| **Status** | Implementado (Windows e Linux) |
| **Decisões** | [ADR-052](../architecture/decisions/052-tempo-trabalhado-por-periodo.md) tempo trabalhado · [ADR-053](../architecture/decisions/053-historico-sete-dias-projecao.md) histórico de 7 dias |
| **Código** | `src/MyTaskApp.Domain/TimeTracking/`, `src/MyTaskApp.Application/TimeTracking/`, `src/MyTaskApp.Desktop/ViewModels/ActiveTimerViewModel.cs`, `src/MyTaskApp.Desktop/ViewModels/TaskTimeLogViewModel.cs`, `src/MyTaskApp.Desktop/ViewModels/TimeEntryEditorViewModel.cs`, `src/MyTaskApp.Application/History/`, `src/MyTaskApp.Domain/Planning/ActivityHistory.cs`, `src/MyTaskApp.Desktop/ViewModels/ActivityHistoryViewModel.cs` |
| **Testes** | `tests/MyTaskApp.Domain.Tests/TimeTracking/`, `tests/MyTaskApp.Application.Tests/TimeTracking/`, `tests/MyTaskApp.Desktop.Tests/ViewModels/TimeTrackingViewModelTests.cs`, `tests/MyTaskApp.Application.Tests/History/GetActivityHistoryHandlerTests.cs`, `tests/MyTaskApp.Domain.Tests/Planning/ActivityHistoryBuilderTests.cs` |

## Cronômetro e períodos

- **Cronômetro por tarefa.** O botão ▶ da linha inicia e o ⏹ para. Enquanto
  corre, a linha mostra o relógio (`00:37:42`) e fica levemente destacada.
  Há **um cronômetro por vez**: iniciar outra tarefa pergunta se é para parar a
  atual ("Parar e iniciar").
- O início fica gravado no banco desde o clique. Fechar o app, reiniciar o
  Windows ou suspender o computador não perde nada: ao voltar, o relógio
  continua contando do início.
- Concluir, cancelar, arquivar ou mandar a tarefa para a lixeira para o
  cronômetro dela.
- A aba **Tempo** da janela da tarefa mostra o registrado, o que está em
  andamento, o gasto contra a estimativa ("4h 30min / 6h · 75%") e o histórico
  por dia, com início, fim, duração, origem (Timer ou Manual) e observação.
  **+ Adicionar tempo** lança um período esquecido. Cada período pode ser
  editado ou excluído, e períodos da mesma tarefa não se sobrepõem.
- No HUD, uma faixa com a tarefa e o relógio fica visível enquanto o
  cronômetro corre.
- Os mesmos períodos alimentam os relatórios de horas do [MCP](../mcp-server.md).

## Histórico dos últimos 7 dias

- O botão de histórico do cabeçalho abre **Histórico · 7 dias**: hoje e os seis
  dias anteriores, cada um com o que foi **concluído** (✓, com a hora e o tempo
  do dia) e o que foi **trabalhado** sem concluir (○, com o tempo do dia).
- Uma tarefa longa aparece em cada dia em que foi trabalhada, com o tempo
  daquele dia. Um período que atravessa a meia-noite conta em cada lado dela.
- No rodapé fica o resumo da semana ("18 concluídas · 24h 35min"). O dia sem
  nada aparece como "Nenhuma atividade".
- Clicar numa linha abre a mesma janela da tarefa, já na aba **Tempo**.
- Com a sincronização ligada, o painel filtra por computador (este, outro ou
  todos).
- O painel abre por cima do checklist e fecha com Esc, no × ou com um clique
  fora. No HUD compacto o botão fica de fora, por falta de espaço.

## Não confirmado no código (2026-10-24)

- O formato exato `00:37:42` do relógio (`ActiveTimerViewModel.ElapsedText`).
- Que o clique no histórico abre a janela já na aba **Tempo**
  (`ActivityHistoryViewModel` abre a tarefa; a seleção da aba não foi localizada).
