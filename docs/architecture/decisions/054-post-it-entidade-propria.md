# ADR-054 — Post-it: o que ainda não merece virar tarefa

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ui-desktop |
| **Registrado** | 2026-10-07, commit `aa15a2f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | StickyNote, post-it, StickyNoteTint, StickyNoteWindowManager, Transformar em tarefa, Ctrl+Alt+N, fixar |
| **Relações** | relacionado: [ADR-007](007-inbox-e-estado-nao-entidade.md), [ADR-013](013-captura-rapida-uma-linha-uma-tarefa.md), [ADR-017](017-janela-principal-como-widget.md), [ADR-020](020-ciclo-de-vida-estado-derivado.md), [ADR-025](025-etiquetas-n-n-bolinhas.md), [ADR-038](038-anotacao-markdown-vscode-e-previa.md), [ADR-041](041-temas-sete-paletas-e-automatico.md), [ADR-048](048-hud-visivel-sem-atrapalhar.md) |
| **Código** | `src/MyTaskApp.Domain/StickyNotes/StickyNote.cs`, `src/MyTaskApp.Desktop/StickyNotes/StickyNoteWindowManager.cs`, `src/MyTaskApp.Desktop/Theming/StickyNoteTint.cs`, `src/MyTaskApp.Application/StickyNotes/ConvertStickyNoteToTask.cs` |

**Contexto:** a captura rápida (ADR-013) transforma toda linha em tarefa para
hoje. Isso é certo para "comprar pão" e errado para "falar com o João sobre a
API", "ideia de cache" ou "perguntar sobre homologação": coisas que se quer
ver, e não marcar como feitas. Elas acabavam no checklist, contando como
pendência, ou não eram registradas.

**Decisão:** `StickyNote` é um agregado próprio (`Domain/StickyNotes`), uma
janela por post-it, e a única ponte com as tarefas é "Transformar em tarefa".

## Entidade própria, e não um `TaskItem` sem data

O ADR-007 escolheu "Inbox é estado, não entidade", porque o item do inbox **é**
uma tarefa ainda não triada. O post-it não é: não tem ocorrência, prioridade,
prazo, lembrete nem conclusão, e não pode aparecer em ATRASADAS, AGORA, HOJE,
PRAZOS ou CONCLUÍDAS. Como `TaskItem`, cada consulta do quadro ganharia um
"menos os post-its", e cada caso de uso de tarefa uma recusa para eles. A
conversão é explícita, e por isso a entidade é outra.

**Três grupos de estado, separados:**

- **Conteúdo:** texto, etiqueta (uma só), cor, destaque e opacidade.
- **Janela:** aberta, fixada, posição e tamanho.
- **Ciclo de vida:** `ArchivedAt` e `DeletedAt` independentes, com o estado
  derivado (`StickyNoteLifecycle`), igual ao ADR-020. Arquivado e na lixeira
  são somente leitura. Restaurar da lixeira devolve ao arquivo quem estava lá.

**A geometria mora no banco, e não no `widget.json`.** O ADR-017 pôs a posição
do painel num arquivo porque ela "não é dado do usuário". Aqui são N janelas
que nascem e morrem com a linha. Um arquivo à parte teria de ficar em sincronia
com o banco a cada criação, conversão e expurgo, e um post-it apagado deixaria
uma posição órfã. A convenção é a do widget: posição em pixels físicos e tamanho
em DIP.

**Sem trilha de auditoria.** `TaskAuditEntries` existe para investigar o que o
usuário não desfaz sozinho num checklist. Um post-it apagado não deixa pergunta
a responder depois, e uma linha por post-it descartado afogaria a trilha. A
conversão grava o `Created` da tarefa, que já está na trilha.

**`ConvertedTaskId` não tem chave estrangeira**, pela mesma razão da auditoria
do ADR-020: a tarefa pode ser expurgada, e a referência continua dizendo o que
aconteceu.

## Fechar é esconder, e em branco é descartar

O X, o Esc, o Ctrl+W e o Alt+F4 passam pelo mesmo caminho: gravar o texto,
gravar a posição e só então marcar a janela como fechada. Encerrar o app não
marca nada como fechado, porque um post-it fixado tem de voltar.

**Um post-it em branco é apagado ao fechar.** "Novo Post-it" seguido de X é um
arrependimento, e uma lista cheia de "Post-it vazio" seria o oposto de captura
sem burocracia. É a única exclusão sem lixeira, e só vale para o que não tem
nada escrito. Quem decide é o agregado (`EnsurePermanentDeletionIsAllowed`).

**Excluir não pergunta; excluir definitivamente pergunta.** Mandar para a
lixeira se desfaz na aba Lixeira, e o prazo é o mesmo dos checklists, na mesma
varredura (`RunLifecycleMaintenance`), com a mesma reconferência em memória.

## Só os fixados reabrem

Na subida do app voltam para a tela os post-its **ativos, abertos e fixados**,
e sem roubar o foco. Um post-it solto que ficou aberto continua na lista. Reabrir
todos encheria a área de trabalho de quem faz login, e fixar já é a forma de
dizer "quero ver isto sempre".

## Uma janela por post-it, e nenhum laço

`StickyNoteWindowManager` guarda `Guid → janela`. Nunca há duas janelas do mesmo
post-it, pela mesma razão do dicionário de anotações do `App`: duas telas do
mesmo texto teriam duas versões dele. Não há processo, timer ou consulta por
post-it:

- o texto grava numa pausa de 500 ms da digitação;
- a posição grava 600 ms depois do fim do arrasto;
- os dois gravam na hora quando a janela perde o foco.

Os `DispatcherTimer` de cada janela só correm enquanto há algo pendente. As
gravações de um post-it passam por um semáforo. Sem ele, "fechar" logo depois
de digitar poderia chegar ao banco antes do texto e descartar como "em branco"
um post-it que estava sendo escrito.

**Sem botão na barra de tarefas** (`ShowInTaskbar=False`): cinco post-its não
podem virar cinco botões lá embaixo. O caminho de volta é a lista, no menu do
painel e na bandeja.

**Arrastar só pelo cabeçalho**, com `ElementRole="TitleBar"`, e os botões dentro
dele com `User` (armadilha nº 4 do ADR-017). O texto fica fora da área de
arrasto: selecionar com o mouse não move a janela. As oito alças de
redimensionar viraram o controle `ResizeGrips`, usado pelo widget e pelos
post-its, porque duas cópias em XAML divergiriam na primeira correção.

**Multi-monitor:** a posição restaurada passa pelo `WidgetPlacement.Clamp`. Um
post-it do monitor desligado volta para a tela que mais o cobre. Um post-it novo
nasce no canto superior direito da tela onde o widget está, em cascata.

## Fixar é ordem Z, de cada post-it

`Topmost` só daquele post-it, independente do "sempre no topo" da janela normal
e do HUD (ADR-048). Como no ADR-017, fixar não toca em posição nem tamanho, e há
teste para isso. Fixado, o alfinete fica sempre visível. É a indicação de que
aquele post-it não sai da frente.

## A cor é tinta sobre o tema, com contraste medido

O problema: o usuário escolhe "amarelo" e o app tem sete temas. Um retângulo
amarelo com a letra do tema por cima quebra o Carvão, e letra preta fixa quebra
o Alto contraste. `StickyNoteTint` (em C#, como a paleta do ADR-041) calcula o
desenho:

- **A cor é tinta sobre o cartão do tema.** A matiz entra 16% no escuro e 24% no
  claro. O modo **Atenção** soma 12% e põe uma faixa lateral de 3px.
- **A claridade volta para a faixa em que o texto passa.** A fórmula da WCAG,
  resolvida para o fundo, dá um teto de luminância com letra clara e um piso
  com letra escura, para os três níveis de texto (7:1 no principal, 4,5:1 nos
  de apoio). Sem isso, no Carvão a tinta clareava o fundo, a régua a cortava até
  quase nada, e o "laranja" saía cinza. Há teste que exige a tinta visível, e
  não só legível.
- **O texto é sempre o do tema.** Quem cede é a tinta, nunca a letra.
- **No Alto contraste não há tinta.** A cor vira borda de 2px, ajustada para 3:1
  contra o fundo.
- **Atenção sem cor própria usa o âmbar de atenção do tema**, a semântica fixa
  do ADR-041.

`StickyNoteTintTests` passa todos os temas × sem cor, as 7 cores da paleta, as
12 de etiqueta, branco e preto × Normal e Atenção pelos mesmos mínimos do
ADR-041. A conta de contraste saiu dos testes para `ColorContrast`, para a tela
e o teste usarem a mesma. Como a cor é calculada, um `DynamicResource` não a
alcança: `ThemeController.Changed` avisa os post-its abertos para repintar.

**Cor da etiqueta:** é lida a cada carga, como no ADR-025. Recolorir a etiqueta
repinta quem usa a cor dela (a janela Etiquetas avisa o gerenciador). Com uma
cor própria escolhida, a etiqueta não muda a cor do post-it. Excluir a etiqueta
põe `TagId` em nulo (`SET NULL`), e o post-it volta à cor do tema em vez de
desenhar uma cor que não existe.

**Opacidade** (100/90/80/70) vai só no pincel do fundo, com o piso de 70% do
HUD. O texto nunca fica translúcido.

## Transformar em tarefa sem perguntar nada

`StickyNoteTaskDraft` é pura. A primeira linha não vazia é o título, cortado em
200 caracteres na palavra. O texto inteiro vai para a anotação quando sobrou
algo fora do título. A tarefa nasce **para hoje, sem hora**, pela razão do
ADR-013: ela tem de aparecer na tela que existe. A etiqueta vem junto. O post-it
inteiro convertido vai para os **arquivados**, com `ConvertedTaskId`. Tudo sai
num `SaveChanges` só.

**`TaskCreation` é o caminho único de criar tarefa uma a uma:** lembrete
padrão, armar, registrar e auditar. `CreateTask` e a conversão passam por ele.
Duas cópias divergiriam no dia em que criar tarefa ganhasse uma regra, e o
post-it convertido nasceria sem ela.

**Criar tarefa com a seleção** converte só o trecho, e o post-it fica como
está. A seleção só é lida com o foco no texto. Clicar no ⋯ tira o foco, e a
seleção que importa é a de antes do clique.

**A lista age pela janela quando o post-it está aberto.** Arquivar, converter ou
fixar pela lista um post-it aberto passa pelo `StickyNoteViewModel` dele, que
grava o texto pendente antes. Chamar o caso de uso direto perderia a última
frase.

## Atalho: Ctrl+Alt+N global, ligado por padrão

Ctrl+Shift+N era o pedido, e é o atalho **dentro** do app (painel e post-it).
Como atalho **global**, ele roubaria "nova janela" do VS Code, "janela anônima"
do navegador e "nova pasta" do Explorer. Ctrl+Alt+N não é de nenhum desses. Ao
contrário do Ctrl+Shift+Espaço do ADR-048, ele nasce **ligado**, inclusive para
quem atualiza (o `widget.json` antigo não tem a chave). Em
*Janela e comportamento…* dá para escolher Ctrl+Alt+N, Ctrl+Shift+N ou Desligado.
Se outro programa já for dono da combinação, a escolha volta para Desligado e a
tela diz o motivo.

`WindowsGlobalHotkey` passou a ser uma instância por combinação
(`HotkeyGesture`), cada uma com seu id de `RegisterHotKey`. O `WM_HOTKEY` diz
qual disparou, e o gancho de cada uma só responde ao seu. A dona dos dois
registros continua sendo a `MainWindow`, cuja janela existe mesmo escondida na
bandeja. O `WM_HOTKEY` dá ao processo o direito de trazer uma janela para a
frente, e por isso o post-it abre focado mesmo com outro programa em uso.

## Fora de escopo, de propósito

- **"Lembrar depois".** O post-it não é lembrete. A arquitetura não impede: seria
  um `RemindAt` no agregado e um passo no tique do `ReminderScheduler`, no molde
  do ADR-050.
- **Markdown e links clicáveis.** O editor é um `TextBox` simples. Captura
  rápida não precisa da barra de formatação do ADR-038, e o texto cru vai inteiro
  para a anotação da tarefa, que já é Markdown.
- **Busca na lista**, até a quantidade justificar.
- **Ficar mais discreto parado.** O pedido de "esmaecer quando fixado e sem
  foco" virou só esconder os controles: os botões aparecem com o mouse ou com o
  foco no post-it, e o alfinete fica quando fixado. Esmaecer o texto contraria
  a régua de contraste acima.

**Limites aceitos:**

- sem botão na barra de tarefas, um post-it solto atrás de outras janelas só
  volta pela lista ou pelo Alt+Tab;
- encerrar o app pela bandeja logo depois de digitar, sem a janela perder o
  foco, pode perder os últimos 500 ms de texto. Na prática o clique na bandeja já
  tirou o foco, e isso gravou o texto.

**Testes:**

- Domínio: o agregado inteiro, o rascunho da tarefa e a paleta.
- Application: um teste por caso de uso, a conversão (título, anotação,
  etiqueta, seleção, recusa sem gravar nada) e o expurgo pela varredura.
- Infrastructure, com SQLite de verdade: ida e volta de todos os campos, posição
  e tamanho, a etiqueta recolorida e a excluída, as três listas, o corte da
  prévia no SQL, quem reabre na subida, a conversão de ponta a ponta até o quadro
  de hoje e o upgrade a partir de `TimeEntries`.
- Desktop, headless:
  - papéis de arrasto e alças, e o pino só como ordem Z;
  - o X que não exclui, o Alt+F4 pelo mesmo caminho, a pausa da digitação e o
    foco no texto;
  - uma janela por post-it, a subida só com os fixados, a troca de tema
    repintando e a etiqueta recolorida chegando;
  - a lista agindo pela janela aberta;
  - o atalho registrado, trocado, recusado e solto;
  - o menu da bandeja.
