# ADR-024 — Anotação do item: Markdown próprio, porque o editor pronto é pago

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ui-desktop |
| **Registrado** | 2026-09-22, commit `a85f9a4` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | MarkdownView, TaskItem.Description, TaskNotesWindow, barra de formatação, UnboundedDescription, anotação, RichTextEditor |
| **Relações** | revisto por: [ADR-038](038-anotacao-markdown-vscode-e-previa.md); relacionado: [ADR-020](020-ciclo-de-vida-estado-derivado.md), [ADR-025](025-etiquetas-n-n-bolinhas.md), [ADR-026](026-diretorios-da-etiqueta-alias.md) |
| **Código** | `src/MyTaskApp.Desktop/Notes/MarkdownView.cs`, `src/MyTaskApp.Desktop/Notes/MarkdownDocument.cs`, `src/MyTaskApp.Desktop/Views/TaskNotesWindow.axaml`, `src/MyTaskApp.Infrastructure/Persistence/Migrations/20260922235723_UnboundedDescription.cs` |

**Decisão:** cada item do checklist ganha um texto livre com formatação, aberto
por um **ícone na linha** com clique simples. O texto é **Markdown**, escrito
numa `TextBox` com barra de formatação e lido, depois que a tarefa é concluída,
por um renderizador próprio (`MarkdownView`) que monta `Inline`s do core do
Avalonia. Ele mora em `TaskItem.Description`, que já existia.

**Por que não o `RichTextEditor`:** o pedido dizia "usar o RichTextEditor". O
`Avalonia.Controls.RichTextEditor` e o `Avalonia.Controls.Markdown` são oficiais
e resolveriam a feature inteira, mas os dois exigem **licença Avalonia Pro
paga** — e o app não tem nenhuma dependência paga hoje. O Avalonia 12 não traz
nenhum editor de texto formatado gratuito: o core tem `TextBox` e `TextBlock`, e
só. O custo aceito são os dois arquivos de `Notes/`; o que se ganha é que a
anotação continua sendo **texto puro no banco**, legível por qualquer coisa que
abra o SQLite, em vez de RTF ou de uma árvore serializada presa a um pacote.

**Por que campo nenhum foi criado.** `TaskItem.Description` já existia no
domínio, no schema e no `UpdateTaskHandler` — faltava só a tela; nenhuma
migration. (O campo tinha teto de 4000 caracteres; depois caiu — ver "Limites
aceitos".) Um campo novo custaria duas colunas com o mesmo
significado e duas respostas para "onde fica o texto deste checklist". Como
`UpdateTask` é edição **atômica** de título, descrição e prioridade, a tela
guarda os outros dois na carga e os devolve inalterados: o erro tentador seria
mandar título vazio e apagar o nome da tarefa pelo gesto de anotar algo nela.

**Ícone, e não duplo clique** — o pedido original era duplo clique. Não dá: o
`Tapped` do título já copia o texto (§12), e no Avalonia o primeiro clique de um
duplo clique dispara o `Tapped` **antes** do `DoubleTapped`. O gesto duplo
copiaria e abriria de uma vez, com o balão "Texto copiado." aparecendo por cima.
As saídas eram adiar a cópia pelo intervalo de duplo clique do sistema — meio
segundo de atraso num gesto que hoje é instantâneo — ou tirar a cópia do clique
simples, desfazendo uma feature recente. O ícone não disputa com nada.

**O ícone é o indicador.** Ele segue a discrição do sino (`Opacity=0`, aparece no
`:pointerover` da linha), mas com anotação escrita fica aceso em
`WidgetAccentBrush` mesmo sem o mouse. Sem isso, descobrir onde há texto custaria
abrir item por item. O glifo muda com o estado — "abrir em janela" em
aberto, olho concluída —, que é a única pista, antes do clique, de que a janela
vai abrir só para ler. Já foi um lápis; deixou de ser quando a janela passou a
ter título editável e a aba Desenvolvimento, e "escrever" virou só uma das coisas
que se faz nela.

**Concluída vira leitura, e a barra some.** É a regra do pedido: terminada a
tarefa, a anotação é registro. A barra de formatação **desaparece** em vez de
ficar desabilitada — botão apagado ainda convida ao clique. Reabrir a tarefa
devolve a edição, porque quem desmarcou voltou a trabalhar nela. O leitor usa
`SelectableTextBlock`, e não `TextBlock`: sem a caixa de texto, o usuário
perderia junto a capacidade de copiar um pedaço da própria anotação.

**A janela é por item, e não singleton.** Duas anotações diferentes podem estar
abertas ao mesmo tempo, então o truque do "X que esconde" do ADR-020 não se
aplica — ele existe para quem tem uma instância só. Quem impede duas janelas do
*mesmo* item é o `App`, que guarda as abertas num dicionário por `TaskId`: duas
telas do mesmo texto teriam duas versões dele, e a última a salvar apagaria a
outra.

**Quatro armadilhas, três delas descobertas por teste:**

1. **Botão focável rouba a seleção.** Clicar num botão da barra tira o foco da
   `TextBox`, e com ele a seleção — o clique em **B** formatava o vazio onde o
   cursor caía. `Focusable="False"` em `Button.tool` é o que faz a barra
   funcionar.
2. **`*` dentro de `**` é metade de outro marcador.** Conferir os caracteres
   colados na seleção faz o botão de itálico *desfazer* o negrito. O que vale é
   o tamanho do bando de asteriscos, pela convenção do próprio Markdown: um é
   itálico, dois são negrito, três são os dois. Daí `***` ser um marcador de
   verdade no parser — é o que a barra escreve quando alguém clica em **B** e
   depois em **I**.
3. **Marcador mais longo primeiro.** Com `*` tentado antes de `**`, todo negrito
   vira um itálico vazio seguido de lixo. Marcador sem par volta a ser literal:
   quem digitou "2 * 3 = 6" não pediu formatação nenhuma.
4. **`IsVisible` não é herdado.** Um botão dentro de um pai escondido continua
   se declarando visível; o teste da barra que some tem de olhar
   `IsEffectivelyVisible`.

**Limites aceitos:** a anotação não tem teto de tamanho. Nasceu com 4000
caracteres, mas anotação é justamente onde cabe o que não coube no título, e
cortá-la seria perder o detalhe; o SQLite guarda TEXT sem limite, então a
migration `UnboundedDescription` só atualiza o snapshot do EF. O rodapé mostra
a contagem sem fração, para não sugerir um limite que não existe. A janela de
gerenciamento de dados mostra `Description` como texto cru, então lá a anotação
aparece com os asteriscos à mostra. E o parser não resolve ênfase aninhada que
encosta no marcador de fora (`**muito *mesmo***`): o par de dentro sai literal
(resolvido no ADR-038, junto com o resto do Markdown do VS Code).
Nenhum dos três aparece pelo caminho que a barra de formatação escreve.
