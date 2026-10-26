# ADR-025 — Etiquetas: N:N com o checklist, bolinhas na linha

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | etiquetas |
| **Registrado** | 2026-09-22, commit `1237985` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | Tag, TaskItemTags, TagColor, SetTaskTags, TagPicker, bolinhas, NOCASE, ColorView |
| **Relações** | relacionado: [ADR-020](020-ciclo-de-vida-estado-derivado.md), [ADR-024](024-anotacao-markdown-proprio.md), [ADR-050](050-prazo-com-aviso-por-degrau.md), [ADR-054](054-post-it-entidade-propria.md) |
| **Código** | `src/MyTaskApp.Domain/Tags/Tag.cs`, `src/MyTaskApp.Domain/Tags/TagColor.cs`, `src/MyTaskApp.Infrastructure/Persistence/Configurations/TaskItemTagConfiguration.cs`, `src/MyTaskApp.Desktop/ViewModels/TaskTagsViewModel.cs` |

**Decisão:** o usuário cria etiquetas (nome + cor) e marca quantas quiser em
cada checklist. `Tag` é um agregado próprio (`Domain/Tags`), e o vínculo é a
tabela `TaskItemTags (TaskItemId, TagId)`, mapeada como coleção de
`TaskItem` (`TaskItem.Tags`, campo `_tags`), no mesmo molde das ocorrências. Na
lista de hoje a linha mostra **só bolinhas coloridas**, com o nome no balão; o
nome por extenso aparece no seletor e na janela "Etiquetas…".

**Ligado ao `TaskItem`, e não à ocorrência.** A etiqueta diz o que a tarefa
*é* ("Financeiro"), não o que aconteceu num dia — o mesmo raciocínio que pôs a
anotação na série (ADR-024).

**Nome e cor moram só em `Tags`.** O vínculo guarda as duas chaves e mais nada:
renomear ou recolorir aparece em todos os checklists na próxima carga do quadro,
sem sincronizar cópia nenhuma. A cor é sempre `#RRGGBB` em maiúsculas
(`TagColor.Normalize`); alfa é recusado porque uma bolinha translúcida some no
fundo escuro.

**Integridade no banco, não só no domínio:**

- a **PK composta** `(TaskItemId, TagId)` é a regra "a mesma etiqueta uma vez
  por checklist". `TaskItem.SetTags` já garante isso, e a chave impede que outro
  caminho escape;
- **cascata dos dois lados.** Excluir uma etiqueta ou expurgar um checklist leva
  os vínculos junto e nunca sobra referência órfã. Excluir não carrega cada
  checklist para tirar a etiqueta: seria uma ida ao banco por checklist;
- `Name` com collation **`NOCASE`** e índice único: "Urgente" e "urgente" são a
  mesma etiqueta. O handler confere antes (`EnsureNameIsFreeAsync`) só para
  devolver uma mensagem legível em vez de erro de constraint. O `NOCASE` do
  SQLite só dobra ASCII, então "Ágil" e "ágil" passam como nomes diferentes.
  Limite aceito.

**`SetTaskTags` recebe o conjunto final, não "adicione esta".** Um handler só
associa e remove, e repetir a chamada não muda nada. O seletor grava **a cada
marca**, e não ao fechar: o refresh de 60 s recria as linhas e fecharia o
seletor com as escolhas pendentes. Por isso o refresh também espera enquanto
um seletor está aberto (`TodayViewModel.IsPickingTags`). Duas marcas rápidas
passam por um `SemaphoreSlim`, porque conjuntos inteiros gravados fora de ordem
fariam o primeiro sobrescrever o segundo. A tela aplica a marca antes de gravar
e desfaz se a gravação falhar. O erro aparece **dentro do seletor**, que é para
onde o usuário está olhando.

**Sem N+1.** `TodayQuery` ganha uma terceira consulta em lote (vínculos + etiquetas
de todos os `taskIds` de uma vez, agrupados em memória). São três idas ao banco
qualquer que seja o tamanho da lista. `TagQuery` traz a contagem de uso como
subconsulta, servida pelo índice em `TaskItemTags.TagId`.

**Na linha, bolinha e não pílula.** Uma pílula com nome por etiqueta
competiria com o título pela largura de 360px. Aparecem **no máximo cinco
bolinhas**, e o resto vira um "+N" com os nomes no balão. O balão da bolinha é
o `ToolTip` nativo, mas **na cor da própria etiqueta** (`ToolTip.tagTip` em
`Widget.axaml`), com `ShowDelay` de 200 ms. `BetweenShowDelay` negativo impede
que correr o mouse pela lista vá abrindo um balão atrás do outro. Duas
armadilhas:

1. **O `DataContext` não chega ao balão.** Um `ToolTip` explícito em
   `ToolTip.Tip` é valor de propriedade, não filho. Um `{Binding Name}` nele
   abria um balão vazio. As amarrações apontam para a bolinha pelo nome
   (`#Dot.((vm:TagChipViewModel)DataContext)`). Só um teste que abre o balão
   pega isso.
2. **O deslocamento padrão é de 20px para baixo.** Ele foi feito para o balão
   que nasce no ponteiro. Com `Placement="Top"` ele empurrava o balão de volta
   por cima da bolinha, e por isso `VerticalOffset` é -4. Sem espaço acima, o
   popup vira para baixo sozinho. Onde o nome aparece sobre a cor (pílulas do seletor e
prévia), o texto é preto ou branco pelo critério de contraste do WCAG
(`TagColor.PrefersDarkText`), não por um limiar de brilho no olho.

**Cor: paleta primeiro, `ColorView` depois.** Doze cores curadas resolvem com
um clique. "Personalizar cor…" abre o `ColorView` do
`Avalonia.Controls.ColorPicker`, que é **oficial e gratuito** (ao contrário do
editor do ADR-024) e só traz controle, sem tema: o `StyleInclude` do tema
Fluent dele está no `App.axaml`. Sem ele, o controle não se desenha.

**Etiquetar ao criar.** A caixa de captura tem o mesmo botão de etiqueta, com
o mesmo seletor (template `TagPicker`, um só para os dois lugares). Lá ele
trabalha em **rascunho** (`TaskTagsViewModel.IsDraft`): marcar só guarda a
escolha, e quem grava é o `QuickCapture`, que recebe as etiquetas e as aplica a
**todas as linhas**. A gravação sai no mesmo `SaveChanges` das tarefas, e uma
etiqueta excluída nesse meio-tempo recusa a captura inteira, sem criar metade.
Escolha na tela, e não `#etiqueta` no texto: seria sintaxe a decorar (§36), e
um "#1" num título viraria etiqueta sem ninguém pedir. Depois de capturar, só o
texto se esvazia: **as etiquetas continuam marcadas** para a próxima captura,
porque quem registra uma leva de "Financeiro" costuma registrar a seguinte com
a mesma etiqueta. Desmarcar é com o próprio seletor. Como a escolha atravessa
capturas, o `Changed` da janela "Etiquetas…" também relê a lista para ela
(`TodayViewModel.RefreshCaptureTagsAsync`): uma etiqueta excluída sai das
bolinhas em vez de recusar a próxima captura, e uma renomeada troca de nome e
cor. Se a captura falhar, texto e etiquetas ficam, para tentar de novo.

**Etiquetar na janela da tarefa.** Logo abaixo do título editável, as
etiquetas aparecem como **pílulas com o nome**, porque ali há largura, ao
contrário da linha de 360px. O botão "Etiquetas" abre o mesmo seletor
(`TaskTagEditorViewModel`). Ele **grava a cada marca**, como o da linha e o card
do prazo (ADR-050), e fica **fora do "Salvar"**, que é do título e da anotação:
um "Cancelar" não desfaz etiqueta. Ao abrir, o seletor relê também **as
etiquetas da tarefa** (`GetBoardTask`), e não só a lista de todas. A janela
fica aberta por horas e `SetTaskTags` grava o conjunto inteiro, então marcar
sobre um retrato velho desfaria em silêncio o que a linha mudou nesse
meio-tempo. Com a tarefa concluída, a janela é somente leitura: as pílulas
ficam, sem "×" nem seletor.

**Janela singleton, com o "X" que esconde** (ADR-020), aberta pelo menu ⋯ do
cabeçalho ou pelo link do seletor vazio. Ela recarrega a cada abertura porque a
contagem de uso muda enquanto está escondida. Cada mudança dispara `Changed`, e
o `App` recarrega o painel para as bolinhas seguirem.
