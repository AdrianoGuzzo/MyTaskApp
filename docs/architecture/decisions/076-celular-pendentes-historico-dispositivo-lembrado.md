# ADR-076 — Celular: pendentes na frente, Histórico numa aba e o dispositivo lembrado

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | mobile |
| **Registrado** | 2026-10-18, commit `9a08abb` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | HistoryViewModel, TaskListViewModelBase, DeviceFocus, task-list-device, Histórico, BasePreset, OriginCatalog.MatchFor, ShowsStatus |
| **Relações** | revisa: [ADR-067](067-celular-dispositivo-da-sincronizacao.md); revisto por: [ADR-079](079-celular-aba-tarefas-enxuta.md); relacionado: [ADR-062](062-lista-por-computador-onde-aparece.md), [ADR-075](075-celular-temas-concluir-e-teclado.md) |
| **Código** | `src/MyTaskApp.Mobile.Core/ViewModels/TaskListViewModelBase.cs`, `src/MyTaskApp.Mobile.Core/ViewModels/HistoryViewModel.cs`, `src/MyTaskApp.Mobile.Core/Presentation/DeviceFocus.cs`, `src/MyTaskApp.Mobile/Views/TaskListView.xaml` |

**Contexto:** a lista do celular (ADR-067) misturava tudo.

1. **Muitos atalhos.** O topo tinha cinco: Todas, Pendentes, Vencidas,
   Concluídas e Canceladas. O padrão era "Todas", e as concluídas dividiam a
   tela com o que falta fazer.
2. **O dispositivo escondido.** O filtro de origem ficava na folha de
   Filtros, a dois toques e um seletor de distância.
3. **O foco não durava.** Nenhuma escolha da lista sobrevivia a fechar o app:
   quem só olha as tarefas do computador do trabalho escolhia de novo a cada
   abertura.

**Decisão:**

1. **A aba Tarefas é só o que falta fazer.** Atalhos Pendentes (o padrão) e
   Vencidas. Concluir pela linha tira a tarefa da lista, e a barra "Desfazer"
   do ADR-075 continua valendo. A linha não escreve mais "Pendente": o status
   só aparece quando foge disso (`TaskRow.ShowsStatus`).
2. **Concluídas e canceladas numa aba própria, "Histórico"**, entre Tarefas e
   Conta.
   - Atalhos Concluídas (o padrão) e Canceladas.
   - Mesma busca, mesma folha de filtros (`filters?for=history`) e o mesmo
     dispositivo.
   - Sem o botão +: o que nasce é pendente.
   - **Uma lista, dois ViewModels.** `TaskListViewModelBase` tem a busca, a
     paginação, a folha, o "Desfazer" e a sincronização.
     `TaskListViewModel` e `HistoryViewModel` só trocam os atalhos e as
     frases. A tela é uma `TaskListView` (ContentView) nas duas páginas.
   - **"Nada com estes filtros" pela aba.** `TaskFilter.BasePreset` diz qual é
     o atalho de partida de cada aba. Só escolher outro atalho estreita a
     lista, e a aba vazia diz "Nada pendente" ou "Nada no histórico".
3. **O dispositivo vira chips no topo, e é lembrado.**
   - **Chips.** Todas, Compartilhadas, Este celular e cada computador da
     conta, acima dos atalhos de status. Saiu da folha de Filtros, e
     `SheetCount` não o conta mais. Sem conta ligada, os chips não aparecem.
   - **Semântica exata (ADR-067).** "Notebook" é só o que está no notebook,
     sem as compartilhadas. É o mesmo `PlacementMatch` de antes.
   - **Um foco para o app.** O `DeviceFocus` (singleton) guarda a escolha das
     duas abas: trocar numa recarrega a outra.
   - **Lembrado.** A escolha fica no `IPreferenceStore`, chave
     `task-list-device` (as `Preferences` do MAUI). Grava a **chave** (`here`,
     `shared` ou o id do computador), e não o filtro. Os ids antigos deste
     celular entram na hora de buscar (`OriginCatalog.MatchFor`). "Todas" apaga
     a chave.
   - **Antes do diretório.** Na abertura, a escolha já filtra mesmo que o
     `SyncDeviceDirectory` ainda não tenha chegado. O chip ganha o nome quando
     ele chega.
   - **Quando não vale.** Um computador que o diretório já lido não conhece
     mais volta a "Todas". Sem conta ligada, a lista mostra tudo, mas a
     escolha não é apagada: religada a conta, volta.
   - **À vista.** Ao abrir, a fileira de chips rola até o escolhido.
   - **"Limpar filtros"** volta ao atalho de partida e a "Todas".

   (O ADR-079 trocou os chips pelo título da lista e levou "Vencidas" para a
   folha de filtros.)

## Alternativas

- **"Notebook" com as compartilhadas**, como o seletor "Mostrando" do desktop
  (ADR-062). Mostra o que aparece naquele computador, mas o usuário pediu o
  filtro exato, que já era o do celular.
- **Agrupar as pendentes por seção** (Vencidas, Hoje, Próximas, Sem data).
  Ficou a lista simples com o atalho Vencidas: menos rolagem e a mesma
  ordenação da folha.
- **Histórico por um botão no topo**, empilhado sobre Tarefas. Uma aba fica a
  um toque de qualquer tela, e a escolha foi do usuário.
- **Não lembrar, como o desktop.** O seletor do desktop não é lembrado
  (ADR-062): lá o padrão "este computador" já é o foco. No celular o padrão é
  "Todas", e o foco tem de ser escolhido; escolher a cada abertura era o
  incômodo.

## Consequências

- O desktop continua sem lembrar o "Mostrando". As duas telas resolvem
  problemas diferentes, e cada uma ficou com o padrão que serve a ela.
- As outras escolhas da lista (atalho, busca, prioridade, etiquetas, ordem)
  continuam valendo só enquanto o app está aberto.
- O celular passa a ter uma porta de preferência no núcleo. O tema do ADR-075
  continua gravando direto nas `Preferences` pela casca.

**Testes:**

- **Mobile.Core:**
  - a aba Tarefas é só de pendentes, sem "Pendente" na linha;
  - concluir manda para o Histórico, e reabrir lá devolve;
  - "Desfazer" nas duas abas;
  - o Histórico separa concluídas e canceladas, e tem a sua folha;
  - os chips só com conta ligada, com filtro exato;
  - a escolha volta num ViewModel novo sobre as mesmas preferências;
  - a escolha filtra antes do diretório chegar;
  - um computador desconhecido, ou a conta desligada, mostram tudo;
  - as duas abas olham o mesmo dispositivo, e "Limpar filtros" volta a
    "Todas";
  - `MatchFor`, `BasePreset` e `SheetCount` sem o dispositivo.
- **`PhoneSyncTests`:** a origem filtrada pelos chips, ponta a ponta.
