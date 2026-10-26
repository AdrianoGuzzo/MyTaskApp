# ADR-026 — Diretórios da etiqueta: o alias é atalho de digitação, não referência

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | etiquetas |
| **Registrado** | 2026-09-23, commit `57bb5ab` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | TagDirectory, TagDirectories, @alias, AliasCompletion, IDirectoryProbe, autocomplete, path |
| **Relações** | revisto por: [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md); relacionado: [ADR-024](024-anotacao-markdown-proprio.md), [ADR-027](027-aba-desenvolvimento-worktree-git.md), [ADR-028](028-comandos-pos-worktree-e-globais.md), [ADR-032](032-correcao-ortografica-do-sistema.md), [ADR-035](035-branch-de-origem-padrao-diretorio.md), [ADR-039](039-arroba-ambientes-e-arquivos-no-agente.md), [ADR-064](064-repositorio-git-do-diretorio-clone.md) |
| **Código** | `src/MyTaskApp.Domain/Tags/TagDirectory.cs`, `src/MyTaskApp.Desktop/Notes/AliasCompletion.cs`, `src/MyTaskApp.Desktop/Notes/AliasCompletionBinder.cs`, `src/MyTaskApp.Application/Abstractions/IDirectoryProbe.cs` |

**Decisão:** cada etiqueta tem N **diretórios** (`TagDirectory`: alias, path,
nome e descrição opcionais), cadastrados no próprio cartão da etiqueta na janela
"Etiquetas…". Na anotação de uma tarefa (ADR-024), digitar `@` abre uma lista com
os aliases das etiquetas **daquela tarefa**. Escolher um item troca o `@texto`
pelo **path real**, e o alias não fica no texto.

**Por que não guardar `@alias` no texto.** Uma referência viva faria cada
anotação depender do cadastro: mudar o path reescreveria o passado, e excluir o
diretório deixaria um `@` órfão que o leitor Markdown não saberia desenhar. O
pedido é explícito: o alias é um *snippet*, e mudar `C:\` para `D:\` depois
**não** altera anotações antigas. A anotação continua sendo texto puro no
banco, como o ADR-024 quis.

**Entidade, e não uma lista de strings na etiqueta.** O diretório tem identidade
(`Id` v7, tabela `TagDirectories`) para ser editado e removido um a um. A
integração com Git (ADR-027) **não** se prende a ele: a tarefa guarda uma cópia
do caminho, pelo mesmo motivo de a anotação guardar o path, e não o alias. Ele é
parte do agregado `Tag`: `AddDirectory`,
`UpdateDirectory` e `RemoveDirectory` passam pela raiz, e excluir a etiqueta leva
as pastas junto (cascata).

**Alias único por etiqueta, não global.** Duas etiquetas podem ter um `@api` cada.
A regra mora no agregado (compara sem maiúsculas) e o índice único
`(TagId, Alias)` com `NOCASE` impede outro caminho. Numa tarefa com as duas
etiquetas, a lista mostra os dois `@api`, cada um com a bolinha e o nome da sua
etiqueta. O alias começa com `@`, e o `@` é posto se faltar. Depois dele vêm só
letras ASCII, dígitos, `.`, `-` e `_`, que são os mesmos caracteres que o
autocomplete reconhece no texto. O path precisa ser absoluto
(`IsPathFullyQualified`), vem sem aspas e sem barra final, e **pode não
existir**.

**A pasta é conferida na exibição, nunca no cadastro.** `IDirectoryProbe` (porta
da Application desde o ADR-027, implementada na Infrastructure; com
`Directory.Exists` sob `Task.Run`, porque um caminho de rede desconectado segura a
resposta) marca ✓/⚠ na lista da etiqueta e "pasta não encontrada" no
autocomplete. A lista aparece antes da conferência.

**Quando a lista abre** (`Notes/AliasCompletion`, puro e testado sem janela):

- o `@` precisa abrir o texto ou vir depois de espaço/pontuação. Em
  `fulano@empresa.com` ele é de um e-mail, e abrir a lista ali atrapalharia;
- o filtro é por **trecho** do alias (e do nome), e não só pelo começo: o
  exemplo do pedido mostra `@eco` achando `@scripts-eco`. O que começa com o
  digitado vem primeiro;
- Escape, clique fora ou aceitar "dispensam" aquele `@`. A lista não reabre
  sobre ele, e um `@` novo reabre. Sem isso, um path com `@` recém-inserido
  reabriria a lista sozinho.

**Teclado antes de tudo.** Com a lista aberta, ↑/↓, Enter, Tab e Escape são
tratados no handler de **túnel** da janela, antes da `TextBox` (Enter quebraria a
linha, Tab tiraria o foco) e antes do Escape que fecha a janela. Os itens não
recebem foco, pelo mesmo motivo do `Button.tool`: o cursor precisa ficar no
texto. O clique aceita no `PointerPressed`. O popup se posiciona no cursor via
`TextLayout.HitTestTextPosition` e `PlacementRect`.

**Os atalhos vêm pela tarefa, a cada ativação da janela.** `ListDirectoriesForTaskAsync`
junta `TaskItemTags → Tags → TagDirectories` numa consulta. Consultar pela
tarefa, e não pelo instantâneo da linha, acompanha etiquetas e diretórios
mudados enquanto a anotação estava aberta. Se a consulta falha, a anotação
continua funcionando, só que sem lista.

**Armadilha:** o `Id` nasce no domínio. Sem `ValueGeneratedNever`, um diretório
acrescentado a uma etiqueta já rastreada chega com chave preenchida, o EF o
toma por existente e gera `UPDATE`, e o `SaveChanges` falha por concorrência.
Só o teste de ida e volta contra SQLite pegou.

**Armadilha — o Popup fecha na hora.** O `IsOpen` do Popup é amarrado nos dois
sentidos. Fechar a lista dispara o `Closed` **dentro** da atribuição, e o
handler de "fechou por fora, então dispensa" rodava enquanto o token ainda
estava lá. Todo fechamento virava dispensa: digitar `@ecx` (sem resultado) e
apagar o `x` não reabria a lista. Por isso `CloseCompletion` zera o token
**antes** de fechar. Há teste de ViewModel que confere a ordem e teste headless
com o Backspace.
