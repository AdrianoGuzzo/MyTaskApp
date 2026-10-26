# ADR-079 — Celular: a aba Tarefas enxuta, o dispositivo no título e a Nova tarefa discreta

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | mobile |
| **Registrado** | 2026-10-20, commit `1db0168` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | DeviceTitle, IDialogService.ChooseAsync, HasSheetFilters, TaskFilter.OverdueOnly, Só as vencidas, Nova tarefa, ShowsPresets, NavBarIsVisible |
| **Relações** | revisa: [ADR-076](076-celular-pendentes-historico-dispositivo-lembrado.md) |
| **Código** | `src/MyTaskApp.Mobile.Core/ViewModels/TaskListViewModelBase.cs`, `src/MyTaskApp.Mobile/Views/TaskListView.xaml`, `src/MyTaskApp.Mobile.Core/Presentation/TaskFilter.cs`, `src/MyTaskApp.Mobile/Adapters/MauiPorts.cs` |

**Contexto:** depois do ADR-076, a aba Tarefas ainda empilhava cinco faixas
antes da primeira tarefa:

- a barra do topo com "Filtros";
- a pílula da sincronização;
- a barra de busca;
- a fileira de chips de dispositivo;
- a fileira Pendentes/Vencidas.

Por cima da lista ficava um "+" redondo de 60 pontos com sombra. O usuário
pediu:

- a tela focada nas pendentes;
- um botão de criar mais discreto;
- um lugar **de destaque** para o dispositivo. Os chips rolavam de lado e
  pareciam mais um filtro entre outros.

**Decisão:**

1. **O dispositivo é o título da lista.**
   - A barra da Shell sai das abas Tarefas e Histórico
     (`Shell.NavBarIsVisible="False"`). O topo é da `TaskListView`: legenda da
     aba ("PENDENTES", "HISTÓRICO") e, grande, o dispositivo em foco com um
     "⌄".
   - Um toque abre a folha de opções do sistema ("Mostrar as tarefas de"),
     com o atual marcado por "✓". É uma nova porta,
     `IDialogService.ChooseAsync`: `DisplayActionSheetAsync` na casca.
   - A folha devolve o texto tocado. Por isso, dois computadores com o mesmo
     nome ganham "(2)" nela.
   - Sem conta ligada, o título é o nome da aba e não abre nada.
   - "Todas" virou "Todos os dispositivos", que se lê bem como título.
   - O resto do ADR-076 continua: o `DeviceFocus`, a chave gravada e o filtro
     exato.
2. **Busca e filtros viram ícones** à direita do título.
   - São vetores (`Path`) com a cor do tema por `DynamicResource`, sem pacote
     de ícones e sem gatilho de cor.
   - A lupa abre a busca já com o teclado. Fechá-la apaga o texto e relê.
     Com texto digitado, a busca continua à vista. "Limpar filtros" também a
     fecha.
   - O funil ganha um ponto quando a folha tem algo ligado
     (`HasSheetFilters`). O leitor de tela ouve "Filtros (2)".
3. **"Vencidas" sai do topo e vai para a folha**, como "Só as vencidas".
   - O critério é o mesmo do quadro Hoje (`TaskFilter.OverdueOnly`), e conta
     no `SheetCount`.
   - A aba Tarefas fica com um atalho só, e a fileira some (`ShowsPresets`).
     O Histórico mantém Concluídas/Canceladas e não mostra a opção.
   - `TaskListPreset.Overdue` deixou de existir.
4. **"＋ Nova tarefa" no rodapé**, como nos Lembretes do iOS.
   - Só texto na cor de destaque (`TextAction`), numa faixa própria abaixo da
     lista. Não cobre nenhuma tarefa e fica ao alcance do polegar.
   - A barra "Desfazer" ocupa a largura toda, sem desviar do antigo "+".

## Alternativas

- **Barra "Mostrando: Notebook ⌄" abaixo do título "Tarefas".** É mais
  explícita, mas custa uma faixa a mais, justo o que se queria tirar.
- **Manter os chips, só maiores.** A escolha é de um toque, mas com muitos
  computadores a fileira rola de lado e esconde o escolhido.
- **Uma tela própria de dispositivos.** Teria espaço para ícones, mas a folha
  do sistema já resolve, e com o visual nativo de cada plataforma.
- **Ícone "+" no topo ou um "+" redondo menor.** O do topo fica longe do
  polegar. O redondo continua cobrindo a última tarefa.
- **Esconder a pílula de sincronização quando está em dia** e **enxugar a
  linha** (sem "Normal", sem a origem repetida). Foram oferecidas, e o
  usuário preferiu manter as duas como estão.

## Consequências

- As telas empilhadas (Filtros, detalhe, formulário) continuam com a barra da
  Shell e o botão de voltar.
- O topo próprio se apoia no `SafeAreaEdges` do .NET 10: a `Grid` raiz
  respeita a barra de status por padrão (`Container`).
- A escolha de "Só as vencidas" vale só enquanto o app está aberto, como as
  outras da folha.

**Testes:**

- **Mobile.Core:**
  - a aba Tarefas tem um atalho só e esconde a fileira; o Histórico mostra a
    dele;
  - o título abre a folha com o atual marcado, escolher filtra e grava, e
    cancelar não muda nada; sem conta, nem abre;
  - dois computadores com o mesmo nome se distinguem na folha;
  - `DeviceTitle` com e sem conta;
  - a busca atrás da lupa: abrir, continuar à vista com texto, fechar
    limpando e relendo, e "Limpar filtros" fechando;
  - "Só as vencidas" pela folha: o critério do quadro, "Filtros (1)", o ponto
    no funil, a folha reaberta com a marca, "Nada com estes filtros", e o
    Histórico sem a opção;
  - `TaskFilter.OverdueOnly` na busca, no `SheetCount` e no `IsNarrowed`.
