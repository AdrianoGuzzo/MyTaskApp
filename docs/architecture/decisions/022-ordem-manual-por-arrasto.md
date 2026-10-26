# ADR-022 — Ordem manual dentro da seção, e um item que descola da lista

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ui-desktop |
| **Registrado** | 2026-09-21, commit `1dafbf8` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | Position, reordenar, arrastar, ReorderOccurrences, ReorderDrag, PlaceAt, IsReordering |
| **Relações** | revisa: [ADR-013](013-captura-rapida-uma-linha-uma-tarefa.md); relacionado: [ADR-010](010-classificacao-hoje-funcao-pura.md), [ADR-014](014-padrao-de-lembrete-no-banco.md), [ADR-017](017-janela-principal-como-widget.md), [ADR-020](020-ciclo-de-vida-estado-derivado.md) |
| **Código** | `src/MyTaskApp.Domain/Tasks/TaskOccurrence.cs`, `src/MyTaskApp.Application/Planning/ReorderOccurrences.cs`, `src/MyTaskApp.Application/Planning/GetTodayBoardHandler.cs`, `src/MyTaskApp.Desktop/Interactions/ReorderDrag.cs` |

**Decisão:** `int? Position` em **`TaskOccurrence`**, numeração **densa por
seção** reescrita inteira a cada solta, e ordenação por
`Position ?? int.MaxValue` como primeiro critério nas quatro seções pendentes.
`null` = nunca foi arrastada.

**Por que na ocorrência e não na série:** a linha que o usuário arrasta é uma
ocorrência, e duas ocorrências da mesma série podem cair em seções diferentes no
mesmo dia. Posição na série ordenaria as duas juntas — ou nenhuma.

**Por que densa e não fracionária:** rebalanceamento e deriva de ponto flutuante
são preços de listas que crescem sem teto. Uma seção de um painel de 360px tem
dezenas de linhas; reescrever todas num `SaveChanges` é determinístico, cabe numa
transação e dispensa manutenção para sempre.

**Quem nunca foi arrastado fica no fim, na ordem de sempre.** É o que encerra a
"Limitação conhecida" do ADR-013 — a captura rápida saindo em ordem alfabética —
**para quem arrastar**, sem mudar nada para quem não arrastar.

**A migration não faz backfill.** Eco do ADR-014 ("Upgrade não arma nada") e do
ADR-020 (arquivamento automático nasce desligado): a atualização não pode
renumerar a lista de ninguém sozinha. `null` em todo mundo significa exatamente
"a ordem de hoje continua valendo", e semear qualquer critério — alfabético, por
exemplo — congelaria para sempre algo que hoje é só o desempate. Há teste em
`UpgradePreservationTests`, contra um banco migrado **até a versão anterior** e
populado por SQL cru, que é a única forma de exercitar o que uma migration faz
com dados que já existiam.

**`Position` não ganha índice.** A ordenação é em memória, no handler (ADR-010),
e a coluna nunca é predicado nem `ORDER BY` em SQL. Um índice aqui seria simetria
com os três vizinhos da migration anterior, não necessidade — e custaria escrita
em todo arrasto.

**O comando carrega a seção inteira**, e não "moveu da casa 3 para a 1": a lista
completa é idempotente (reenviá-la não produz deriva, e a tela grava depois de já
ter movido) e dispensa quem chama de calcular delta nenhum.

**Validar tudo antes de mexer em qualquer coisa.** Reordenar é escrita em dezenas
de agregados de uma vez, então o handler faz duas passadas: a primeira confere
cada ocorrência por `TaskItem.EnsureOccurrenceCanBePlaced` — na forma de
`EnsurePermanentDeletionIsAllowed`, com a regra morando no agregado —, e só a
segunda numera. Sem isso, uma recusa no meio da seção deixaria metade da lista
renumerada em memória, e um `SaveChanges` posterior persistiria a meia-alteração.
É a armadilha que a "Edição atômica" de `TaskItem.Update` evita, agora espalhada
por vários agregados.

**Não há entrada de auditoria.** A trilha do ADR-020 existe para investigar o que
o usuário não desfaz sozinho — arquivar, lixeira, exclusão. Arrastar se desfaz
arrastando de volta, e registrar cada arrasto a afogaria em ruído.

## O que acontece com a posição quando a linha muda de seção

A seção é **derivada em leitura** (`TodayClassifier`), então `Position` é um
ordinal de seção guardado sem a seção. Três respostas, e só a primeira exigiu
código:

1. **CONCLUÍDAS ignora `Position` por completo** — lá a ordem é a da conclusão, e
   nada mais. E `PlaceAt` **recusa ocorrência que não esteja pendente**, o que
   congela a posição de quem concluiu em vez de deixá-la editável e
   silenciosamente descartada pela leitura. O ganho vem de graça e é o melhor
   comportamento possível: **concluir não perde o lugar, e reabrir devolve a
   linha exatamente onde ela estava.** A alça não aparece em CONCLUÍDAS — alça
   que não faz nada é pior do que alça nenhuma.
2. **Reagendar zera a posição**, pela mesma razão pela qual `Reschedule` já
   desarma o lembrete: o lugar era numa fila de outro dia.
3. **Entre ATRASADAS, AGORA e HOJE a posição viaja junto, e isso é aceito.** As
   três são a mesma fila partida pelo relógio. O preço é que o relógio pode pôr
   duas linhas na mesma casa; o desempate de sempre (data/hora) resolve de forma
   determinística, e há teste fixando isso. Limite medido, não descuido.

**Alternativa rejeitada — limpar a posição em `Complete`/`Reopen`:** explicável,
mas faria um clique errado no checkbox apagar o lugar que o usuário acabou de
escolher à mão.

**Revisão do escopo pedido.** A decisão original era "reordenar dentro de
qualquer seção", CONCLUÍDAS inclusive. O que a mudou foi perceber que arrastar
ali faria a lista mentir sobre a ordem em que as coisas foram feitas — e que
recusar comprava, de graça, o "reabrir devolve o lugar" do item 1.

## O item levantado é desenho, não janela

A linha descola da lista: cresce 4%, inclina 2°, ganha sombra e segue o cursor; o
vão é a própria linha, apagada onde estava. A pega é uma alça que aparece no
hover, como o `⋯` — a lista não pode virar barra de botões (§12), mas reordenar
também não pode ser um gesto que ninguém descobre.

**Por que não `DragDrop.DoDragDrop`,** e o motivo é estrutural, não estético: ela
entrega o gesto ao laço de arrasto do SO, **bloqueia** até a solta, dá `DragOver`
em vez de posição por quadro, troca o cursor por um bitmap do sistema e é
cross-process por desenho (`DataObject`) — justamente o que disputaria com o
`ElementRole="TitleBar"` do modo discreto. Captura manual de ponteiro mantém tudo
em processo, por quadro, e **testável**: nada do laço do SO chegaria ao
`AvaloniaHeadlessPlatform`.

**Camada local, e não `OverlayLayer`.** Um `Canvas` dentro do próprio
`TodayView`: as coordenadas do item, as das linhas medidas e as de
`e.GetPosition(this)` viram **um espaço só**, e o item continua dentro do canto
arredondado do painel em vez de pairar sobre a moldura e as alças de
redimensionamento.

**Uma definição de linha, dois desenhos.** O `DataTemplate` saiu para os recursos
e é usado pelo `ItemsControl` e pelo `ContentControl` do item levantado — mesma
razão pela qual o `⋯` abre o `ContextFlyout` da linha em vez de ter menu próprio:
duas cópias em XAML divergiriam no primeiro campo novo.

**Um passo por quadro.** `ReorderDrag.TargetIndex` compara só com o centro dos
vizinhos imediatos. Não oscila quando o cursor para numa divisa, mantém a
animação legível como uma sequência de trocas simples, e num flique rápido
recupera em poucos quadros — quem segue o cursor de verdade é o item levantado; a
lista embaixo só precisa chegar lá antes de o usuário soltar. De quebra, isso
reduz o deslizamento a **um vizinho por vez**, em lugar de medir a lista inteira
antes e depois.

**A solta não recarrega.** Atualização otimista: a coleção já se moveu e o item
ainda está pousando, e um `LoadAsync` recriaria todos os `TaskRowViewModel` no
meio da animação. Falhou, desfaz o movimento e mostra a mensagem — e **não**
recarrega, o que apagaria a mensagem no mesmo gesto que a produziu. O quadro em
memória (`_board`) é atualizado junto; sem isso, fixar o painel (`HideCompleted`,
ADR-017) remonta a lista a partir do quadro velho e ressuscita a ordem antiga.

**Cinco armadilhas, registradas porque falham em silêncio:**

1. **`IsVisible="False"` na linha de origem mata o vão.** O `StackPanel` a tira
   do fluxo, a lista sobe um degrau e o buraco deixa de existir. Tem de ser
   `Opacity = 0`.
2. **`TransformOperationsTransition` só interpola `TransformOperations`.**
   Atribuir um `TranslateTransform` faz a transição não acontecer, sem avisar. E
   a transição precisa ficar **desligada** enquanto o item segue o cursor —
   ligada, ele anda 180ms atrás da mão. Some-se a isso que
   `TransformOperations.Parse` lê números: em pt-BR, `"1,04"` quebraria o parse,
   então a formatação é `CultureInfo.InvariantCulture` (o app roda com
   `InvariantGlobalization=false`, ADR-002).
3. **Captura na alça se perde.** O container é reposicionado pelo `Move` da
   coleção, e container destacado da árvore derruba a captura. A captura é no
   `TodayView`, que nunca se move — e `OnPointerCaptureLost` cancela o arrasto,
   senão uma captura perdida deixaria o item levantado na tela para sempre.
4. **O refresh de 60 s do ADR-017 atropela o arrasto.** `LoadAsync` faz
   `Sections.Clear()`, e um arrasto atravessa a fronteira do tique com
   facilidade: os containers sumiriam debaixo do ponteiro. `IsReordering` é o que
   faz o tique passar direto.
5. **Esc não chega sozinho.** O foco está na caixa de captura (ADR-013), então o
   cancelamento escuta `KeyDown` no `TopLevel`, em **túnel**, e só durante o
   arrasto — handler esquecido lá é vazamento de sintoma mudo.

**`ElementRole="User"` é declarado na alça**, e não herdado da linha: é a
armadilha nº 4 do ADR-017, que decide no hit-test **não-cliente**, antes de o
ponteiro chegar ao Avalonia — `e.Handled = true` não a impede. O `Handled`
continua lá como rede de segurança para o `BeginMoveDrag` de
`MainWindow.axaml.cs`. O teste que guarda isso afirma o **papel declarado**, e
não o clique, porque no headless não há hit-test não-cliente e um clique simulado
passaria com qualquer valor.

**A alça é um `Border`, não um `Button`:** botão por linha herdaria foco e estado
`:pressed` que atrapalham o gesto, e há testes headless que contam botões por
janela.

**Limites aceitos:**

- se a gravação falhar **depois** do pouso, a lista volta sozinha para a ordem
  anterior enquanto a faixa de erro aparece — um pulo visível. Segurar a animação
  esperando o banco faria o gesto parecer travado em todo arrasto, para evitar um
  susto que quase nunca acontece;
- as seções com horário podem ficar fora de ordem cronológica. Foi escolha
  explícita: o painel deixa de responder "o que vem a seguir" pela posição, e
  passa a responder pelo rótulo de hora, que continua na linha.

**Fora de escopo, e de propósito: Alt+↑/Alt+↓ na linha focada.** O gesto é barato
e reusaria o mesmo comando; o que não é barato é o **modelo de foco** que a lista
precisaria — linha focável, visual de foco, ordem de Tab por dezenas de linhas e
convivência com a captura que toma o foco na abertura (ADR-013). Fica registrado
aqui para a omissão ler como decisão.
