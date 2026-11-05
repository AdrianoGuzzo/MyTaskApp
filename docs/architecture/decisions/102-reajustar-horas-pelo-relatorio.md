# ADR-102 — Reajustar as horas pelo relatório: a tarefa abre os períodos do bloco, com as regras da aba Tempo

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ciclo-de-vida |
| **Registrado** | 2026-11-05 |
| **Palavras-chave** | reajustar horas, OpenTaskCommand, ReportEntryRow, EntriesPanel, GetTimeEntries por tarefa, UpdateTimeEntry, DeleteTimeEntry, AddTimeEntry, ITimeEntryEditor, HoursReportViewModel.Changed |
| **Relações** | revisa: [ADR-097](097-relatorio-de-horas-dia-mes-ano.md); relacionado: [ADR-052](052-tempo-trabalhado-por-periodo.md), [ADR-008](008-domainexception-falha-que-o-usuario-le.md) |
| **Código** | `src/MyTaskApp.Desktop/ViewModels/HoursReportViewModel.cs`, `src/MyTaskApp.Desktop/Views/HoursReportWindow.axaml`, `src/MyTaskApp.Desktop/Views/HoursReportWindow.axaml.cs`, `src/MyTaskApp.Desktop/App.axaml.cs` (`SetUpHoursReport`) |

**Contexto:** o relatório de horas (ADR-097) mostra onde foram as horas, mas
deixou de fora, de propósito, editar lançamentos por ele. É no relatório que se
nota o dia com horas de menos (o cronômetro que não foi ligado), de mais (o que
ficou ligado) ou na tarefa errada. Para corrigir, era preciso achar a tarefa na
lista, abrir a janela dela e procurar o dia na aba Tempo. Isso nem sempre dá:
a tarefa pode já não estar na lista do dia.

**Decisão:**

1. **A tarefa do painel abre os períodos dela.** Clicar numa tarefa de "Horas
   por tarefa" troca o painel pelos períodos dessa tarefa que tocam o bloco
   escolhido, ou o período inteiro sem escolha. Cada período mostra o dia, o
   início e o fim, a duração, a origem e a observação. **‹ Tarefas** ou Esc
   voltam à lista.
2. **A tarefa fica aberta enquanto se navega.** Trocar de bloco, de período ou
   de escala mostra os períodos dela no lugar novo, inclusive num dia sem
   horas, onde mais se lança o esquecido.
3. **Mesmas ações, mesmos casos de uso, mesmo diálogo da aba Tempo (ADR-052).**
   - O lápis corrige com `UpdateTimeEntry`.
   - A lixeira exclui com `DeleteTimeEntry`, depois de perguntar com o foco em
     Cancelar.
   - **+ Adicionar tempo** lança com `AddTimeEntry`.

   O diálogo é o `ITimeEntryEditor`, e a recusa do domínio (sobreposição, fim
   antes do início, futuro) aparece nele, que fica aberto (ADR-008). O
   cronômetro que corre não se edita, como na aba.
4. **O período novo nasce onde o usuário está.** Com um dia passado escolhido
   na escala de dias, o diálogo abre nesse dia, das 9h às 10h. Senão, abre na
   última hora até agora, como na aba Tempo. Ele entra na ocorrência do período
   mais recente que se viu da tarefa. Numa tarefa recorrente, é a ocorrência em
   que se trabalhou por último.
5. **Os períodos vêm do `GetTimeEntries` filtrado pela tarefa**, com o recorte
   de cada um no bloco (`DurationInRange`). O período que passa da borda diz o
   que conta e o tamanho inteiro ("1h de 3h"). O teto da consulta é 500
   períodos, e passando disso o painel avisa quantos ficaram de fora.
6. **Depois de gravar**, o relatório recarrega (números, tarefas e períodos) e
   avisa a lista (`Changed`), que recarrega o total e o relógio da linha.

**Por quê:** a regra de um período já existe e já é testada na Application.
O relatório só precisava de um caminho até ela. Repetir os mesmos casos de uso
e o mesmo diálogo mantém uma regra só, e a auditoria de cada mudança sai igual
à da aba Tempo.

## Alternativas

- **"Ajustar o total do dia para X horas".** Exigiria inventar o período: onde
  ele começa, qual encurtar, em que ocorrência. O banco guarda períodos, não
  totais (ADR-052), e um total "ajustado" viraria horários fictícios na aba
  Tempo e na auditoria.
- **Abrir a janela da tarefa na aba Tempo.** A aba mostra a ocorrência
  inteira, sem o recorte do bloco, e a janela da tarefa pede a linha da lista,
  que a tarefa de um mês passado pode não ter mais.
- **Editar direto na tabela.** A tabela tem uma linha por bloco, não por
  período. Editar um total ali cai no mesmo problema do total ajustado.

## Consequências

- Um clique no painel agora faz algo. A dica embaixo da lista diz o quê.
- O painel faz uma consulta a mais quando há tarefa aberta, também no refresh
  periódico. É uma tarefa num bloco: poucas linhas.
- Gravar recarrega o relatório duas vezes: a recarga dele e a que vem da
  lista. O contador de cargas descarta a resposta superada.

## Fora de escopo, de propósito

Mover um período para outra tarefa, editar vários de uma vez, desfazer, lançar
numa tarefa sem horas no período (ela não aparece no painel; o caminho é o
menu da linha), e o reajuste no app Android e pelo MCP (lá já existem
`time_entry_create`, `time_entry_update` e `time_entry_delete`).
