# Tempo trabalhado, histórico de 7 dias e relatório de horas

| | |
|---|---|
| **Status** | Implementado (Windows e Linux) |
| **Decisões** | [ADR-052](../architecture/decisions/052-tempo-trabalhado-por-periodo.md) tempo trabalhado · [ADR-053](../architecture/decisions/053-historico-sete-dias-projecao.md) histórico de 7 dias · [ADR-097](../architecture/decisions/097-relatorio-de-horas-dia-mes-ano.md) relatório de horas |
| **Código** | `src/MyTaskApp.Domain/TimeTracking/`, `src/MyTaskApp.Application/TimeTracking/`, `src/MyTaskApp.Desktop/ViewModels/ActiveTimerViewModel.cs`, `src/MyTaskApp.Desktop/ViewModels/TaskTimeLogViewModel.cs`, `src/MyTaskApp.Desktop/ViewModels/TimeEntryEditorViewModel.cs`, `src/MyTaskApp.Application/History/`, `src/MyTaskApp.Domain/Planning/ActivityHistory.cs`, `src/MyTaskApp.Desktop/ViewModels/ActivityHistoryViewModel.cs`, `src/MyTaskApp.Desktop/Reports/`, `src/MyTaskApp.Desktop/ViewModels/HoursReportViewModel.cs`, `src/MyTaskApp.Desktop/Views/HoursReportWindow.axaml` |
| **Testes** | `tests/MyTaskApp.Domain.Tests/TimeTracking/`, `tests/MyTaskApp.Application.Tests/TimeTracking/`, `tests/MyTaskApp.Desktop.Tests/ViewModels/TimeTrackingViewModelTests.cs`, `tests/MyTaskApp.Desktop.Tests/ViewModels/RowTimeEntryTests.cs`, `tests/MyTaskApp.Application.Tests/History/GetActivityHistoryHandlerTests.cs`, `tests/MyTaskApp.Domain.Tests/Planning/ActivityHistoryBuilderTests.cs`, `tests/MyTaskApp.Desktop.Tests/Reports/`, `tests/MyTaskApp.Desktop.Tests/ViewModels/HoursReportViewModelTests.cs`, `tests/MyTaskApp.Desktop.Tests/ViewModels/HoursReportWindowTests.cs` |

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
- **Lançar horas sem abrir a tarefa.** No menu `⋯` da linha (ou clique
  direito), **Lançar horas** tem atalhos de um clique: **15 min**, **30 min**,
  **1 h** e **2 h**. Cada um grava o período que acabou agora, terminando no
  minuto atual; se começar antes da meia-noite, começa no dia anterior. A
  confirmação diz o que entrou ("30min lançados (13:30 → 14:00).") e o total
  da linha se atualiza. **Outro período…** abre o mesmo diálogo do
  **+ Adicionar tempo**. Um período que se sobrepõe a outro da tarefa (por
  exemplo, com o cronômetro dela correndo) é recusado com a mensagem do motivo.
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
- **Ver relatório completo**, no rodapé, fecha o painel e abre o relatório de
  horas.

## Relatório de horas

O histórico completo das horas lançadas, numa janela própria
([ADR-097](../architecture/decisions/097-relatorio-de-horas-dia-mes-ano.md)).

- **Como abrir:** menu **Mais opções → Relatório de horas…**, **Ctrl+Shift+H**
  com o painel em foco, ou **Ver relatório completo** no histórico de 7 dias.
- **Escalas:**
  - **Dia:** os dias de um mês.
  - **Mês:** os meses de um ano, com o mapa de calor do ano em cima.
  - **Ano:** do primeiro ano com horas até o atual.
- **Navegar:**
  - **‹** e **›** (ou PageUp e PageDown) trocam o período, sem ir ao futuro.
  - **Hoje** volta ao período atual.
  - A trilha **Anos › 2026 › outubro** sobe de escala.
  - Trocar de escala mantém a data em foco: o mês escolhido vira os dias dele.
- **Indicadores do período:**
  - o total, com a variação contra o mesmo trecho do período anterior
    ("▲ 12% vs 1–27 de setembro");
  - a média por dia trabalhado;
  - os dias trabalhados dos que já passaram ("18 de 27");
  - o maior dia, mês ou ano.
- **Gráfico:**
  - uma barra por bloco, com a linha tracejada da média;
  - sábado e domingo com outro fundo, e o bloco atual em destaque;
  - passar o mouse mostra o balão ("ter, 14/10 · 6h 20min · 4 lançamentos");
  - clicar escolhe o bloco, e clicar de novo desfaz;
  - o duplo clique (ou Enter) desce de ano para meses e de mês para dias;
  - pelo teclado: ←/→, Home e End escolhem, e Esc desfaz.
- **Tabela:**
  - as mesmas horas, uma linha por bloco: período, horas com barra
    proporcional, dias trabalhados, lançamentos e % do total;
  - a escolha é a mesma do gráfico, e a linha de total fica no fim;
  - blocos sem horas aparecem apagados.
- **Mapa de calor** (escala Mês): um quadrado por dia, semanas de domingo a
  sábado. A cor sobe em faixas fixas: sem horas, menos de 2h, de 5h, de 8h, e
  8h ou mais. Clicar num dia abre o mês dele, com o dia escolhido.
- **Horas por tarefa**, ao lado: as tarefas do bloco escolhido, ou do período
  inteiro, da que mais teve horas para a que menos teve, com a parte de cada uma.
- **Filtros:**
  - a etiqueta restringe tudo às horas das tarefas com ela;
  - com a sincronização ligada, o computador é o mesmo seletor da lista.
- **Exportar CSV** salva os blocos do período numa planilha que o Excel em
  pt-BR abre direto (ponto e vírgula, vírgula decimal, acentos certos).
- As horas seguem as regras do resto do app:
  - um período que vira a noite conta em cada dia o que coube nele;
  - o cronômetro em andamento conta até agora, e a lixeira fica de fora.
- Aberta, a janela se atualiza sozinha quando as horas mudam: cronômetro,
  lançamento, MCP ou outro computador.

## Não confirmado no código (2026-10-24)

- O formato exato `00:37:42` do relógio (`ActiveTimerViewModel.ElapsedText`).
- Que o clique no histórico abre a janela já na aba **Tempo**
  (`ActivityHistoryViewModel` abre a tarefa; a seleção da aba não foi localizada).
