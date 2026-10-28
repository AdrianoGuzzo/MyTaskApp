# ADR-097 — Relatório de horas: dia, mês e ano numa janela própria, agregado na hora

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ciclo-de-vida |
| **Registrado** | 2026-10-28 |
| **Palavras-chave** | GetHoursHistory, HoursScale, HoursBucket, LocalDayGrid, HoursReportWindow, HoursBarChart, HoursHeatmap, relatório de horas, mapa de calor, CSV |
| **Relações** | revisa: [ADR-053](053-historico-sete-dias-projecao.md); relacionado: [ADR-002](002-hora-de-parede-no-dominio.md), [ADR-041](041-temas-sete-paletas-e-automatico.md), [ADR-052](052-tempo-trabalhado-por-periodo.md), [ADR-059](059-servidor-mcp-local-loopback.md), [ADR-062](062-lista-por-computador-onde-aparece.md) |
| **Código** | `src/MyTaskApp.Domain/TimeTracking/LocalDayGrid.cs`, `src/MyTaskApp.Application/TimeTracking/GetHoursHistory.cs`, `src/MyTaskApp.Application/TimeTracking/HoursHistoryCsv.cs`, `src/MyTaskApp.Desktop/Reports/`, `src/MyTaskApp.Desktop/ViewModels/HoursReportViewModel.cs`, `src/MyTaskApp.Desktop/Views/HoursReportWindow.axaml` |

**Contexto:** o histórico de 7 dias (ADR-053) responde "o que eu fiz esta
semana", e deixou de fora, de propósito, gráficos, histórico ilimitado,
comparação e exportação. Faltava ver todas as horas lançadas: quanto em cada
dia de um mês, em cada mês de um ano e em cada ano, em gráfico ou em tabela, e
levar isso para uma planilha. O resumo do MCP (`GetTimeSummary`, ADR-059)
agrupa por dia, mas tem teto de 366 dias e não sabe somar por mês nem por ano.

**Decisão:**

1. **Janela própria**, "Relatório de horas", aberta pelo menu **Mais opções**,
   pelo **Ctrl+Shift+H** e pelo **Ver relatório completo** do histórico de 7
   dias. O histórico continua sendo o relance num flyout. O relatório pede
   espaço: indicadores, gráfico, mapa de calor e tarefas lado a lado.
2. **Três escalas, um caso de uso.** `GetHoursHistory(Scale, Anchor, TagId,
   Device)` devolve os blocos já escritos:
   - **Dia:** os dias do mês.
   - **Mês:** os 12 meses do ano, mais os dias do ano para o mapa de calor.
   - **Ano:** do primeiro ano com horas até o atual.

   Os blocos sem horas e os futuros entram, para o gráfico não pular. "Hoje",
   "anterior" e "próximo" vêm na resposta; a tela não tem relógio.
3. **Agregado na hora, sobre o que está gravado**, como no ADR-053: sem tabela
   de totais, sem índice novo, sem migration.
   - Uma leitura só de `ITimeEntryReportQuery` traz o período e o trecho anterior.
   - `LocalDayGrid` reparte cada período pelos dias locais. As meias-noites vêm
     de `IUserClock.ToInstant` (ADR-002), então o dia de 23 ou 25 horas sai certo.
   - Os dias somam em meses e anos.
   - O período que vira a noite conta em cada dia o que coube nele, e o lançamento
     conta uma vez em cada bloco que tocou.
   - O cronômetro correndo conta até agora, e a lixeira fica de fora.
   - O `ByDay` do `GetTimeSummary` passou a usar a mesma grade.
4. **O teto de 366 dias não vale aqui.** A resposta só traz totais, nunca a
   lista de períodos. Na escala de anos, o histórico inteiro passa pela memória.
   Num banco pessoal são alguns milhares de linhas por ano. Se um dia pesar, a
   saída é uma projeção mais estreita na consulta (só tarefa e instantes), sem
   mudar a tela.
5. **Comparação justa.** O período atual, ainda pela metade, compara com o
   mesmo trecho do anterior: 1–27 de outubro contra 1–27 de setembro, com o dia
   limitado ao tamanho do mês, e 2026 até 27/10 contra 2025 até 27/10. Um
   período já encerrado compara inteiro com inteiro. Sem horas antes, não há
   porcentagem. A variação é escrita em cor neutra: trabalhar mais não é
   "melhor".
6. **Gráficos desenhados pelo app, sem pacote.** `HoursBarChart` e
   `HoursHeatmap` são `Control` com `Render`.
   - **Cores e tema.** Cada cor é uma propriedade ligada a um
     `DynamicResource Widget…Brush`, no padrão do `MarkdownView`. Trocar entre os
     sete temas (ADR-041) troca o pincel e redesenha. O `ThemeController` troca
     o dicionário inteiro, e `ActualThemeVariantChanged` nem dispararia entre
     dois temas claros.
   - **Mapa de calor.** Os níveis são o pincel de destaque com quatro
     opacidades, e as faixas são fixas (0, <2h, <5h, <8h, ≥8h): o mesmo dia tem
     a mesma cor em qualquer ano.
   - **Uso.** O clique vale para a coluna inteira, e o teclado anda pelos blocos.
     A tabela tem os mesmos números e é a leitura acessível completa.
7. **Horas por tarefa** do bloco escolhido vêm do `GetTimeSummary` agrupado por
   tarefa: um bloco cabe no teto. As do período inteiro vêm na própria resposta.
8. **CSV para o Excel em pt-BR.** Ponto e vírgula, vírgula decimal, CRLF,
   UTF-8 com BOM e aspas no padrão RFC 4180. As colunas são `Período;Início;Fim;Horas;Duração;Lançamentos;% do total`.
   Os blocos sem horas entram e os futuros não. O seletor de "Salvar como" é da
   janela (`SystemPickers.SaveTextFileAsync`).
9. **Mesmo seletor de computador** da lista (ADR-062), como o histórico: trocar
   no relatório troca a lista. Aberta, a janela recarrega a cada recarga da
   lista. Isso cobre o cronômetro, os lançamentos, o MCP e a sincronização, e
   mantém o bloco escolhido.

**Por quê:** as horas por dia já eram uma projeção correta e testada. Somar por
mês e ano é a mesma projeção, um passo adiante, e não justifica outra fonte de
verdade. Sem pacote de gráfico, o tema segue os tokens, nada novo entra em
`Directory.Packages.props`, e o desenho é testável no headless.

## Alternativas

- **Ampliar o flyout de 7 dias.** O espaço do flyout não comporta gráfico,
  tabela e tarefas, e o checklist ficaria coberto.
- **Somar por mês e ano no `GetTimeSummary`.** O resumo devolve os ids de cada
  período, pensado para o MCP conferir de onde veio cada total. Num ano inteiro,
  isso é muito texto, e o teto de 366 dias existe por isso.
- **Agrupar no SQL.** Os instantes são ticks UTC (ADR-011), e o dia local
  depende do fuso e do horário de verão. Agrupar no banco exigiria repetir o
  `IUserClock` em SQL.
- **LiveCharts2 ou ScottPlot.** Trariam SkiaSharp, poderiam atrasar o suporte a
  cada versão do Avalonia, e as cores de cada tema teriam de ser repassadas a
  eles. O que se precisa (barras, média, mapa de calor) cabe em dois controles.
- **DataGrid na tabela.** Seria mais um pacote e mais um tema para manter. Um
  `ListBox` com colunas fixas basta, e a seleção é a mesma do gráfico.

## Consequências

- A escala de anos lê o histórico inteiro a cada carga, e a janela aberta
  recarrega junto com a lista (também no refresh periódico). Para um banco
  pessoal, isso é aceitável, e mantém o cronômetro em dia sem relógio próprio.
- `HoursBucket.Summary` e os rótulos vêm prontos da Application
  (`ReportCalendar`, com os nomes dos meses fixos em pt-BR). A tela não formata
  datas.
- O MCP continua com `time_entry_get_summary` como está. O relatório não é
  ferramenta MCP.

## Fora de escopo, de propósito

Agrupar por mês ou ano no MCP, semana como escala, metas e jornada esperada,
horas por etiqueta no painel, PDF, editar ou abrir lançamentos e tarefas pelo
relatório, exportar as horas por tarefa, e o relatório no app Android.
