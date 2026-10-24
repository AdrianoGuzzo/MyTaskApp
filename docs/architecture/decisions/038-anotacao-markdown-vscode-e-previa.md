# ADR-038 — Anotação: Markdown do VS Code e pré-visualização enquanto escreve

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ui-desktop |
| **Registrado** | 2026-09-26, commit `271e48b` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | MarkdownDocument, MarkdownView, pré-visualização, lado a lado, Ctrl+Shift+V, CommonMark, tabela, link clicável |
| **Relações** | revisa: [ADR-024](024-anotacao-markdown-proprio.md); relacionado: [ADR-054](054-post-it-entidade-propria.md) |
| **Código** | `src/MyTaskApp.Desktop/Notes/MarkdownDocument.cs`, `src/MyTaskApp.Desktop/Notes/MarkdownView.cs`, `src/MyTaskApp.Desktop/Views/TaskNotesWindow.axaml`, `src/MyTaskApp.Desktop/ViewModels/TaskNotesViewModel.cs` |

**Decisão:** o leitor da anotação (ADR-024) passa a desenhar o que o preview do
VS Code desenha, e a janela ganha três modos enquanto a tarefa está em aberto:
**Escrever**, **Visualizar** e **Lado a lado**. `Ctrl+Shift+V` alterna entre
escrever e visualizar, o mesmo atalho do VS Code. Concluída, a anotação continua
sendo só leitura, sem os botões de modo.

**Por que o leitor antigo "não formatava".** `MarkdownDocument` era um
subconjunto: `#`, `##`, listas de um nível, `**`, `*`, `__` e `~~`. O resto
aparecia cru. `###` virava parágrafo com os `#` à mostra. Crase, bloco de
código, citação, link, tabela, `- [ ]` e `---` também apareciam como texto. O
recuo de uma sublista era jogado fora, e ela se juntava à lista de cima. O
parser agora reconhece:

- títulos de 1 a 6, também sublinhados com `===` e `---`;
- listas aninhadas pelo recuo, com numeração por nível que começa no número
  do primeiro item, como no CommonMark;
- tarefas `- [ ]` / `- [x]`, citação (lida de novo como documento, então cabe
  lista e código dentro), bloco de código com ```` ``` ```` ou `~~~`, linha
  horizontal e tabela do GitHub com alinhamento;
- código entre crases, link `[texto](url)`, imagem (vira link para ela),
  `<url>` e endereço solto, `_itálico_` e escape com `\`.

A ênfase agora segue a regra de flanco: `2 * 3 * 4` é conta, não itálico, e
`nome_de_variavel` não vira itálico. Isso resolveu o limite do ADR-024: a ênfase
encostada no marcador de fora (`**muito *mesmo***`) agora sai certa.

**Três diferenças do VS Code, de propósito:**

- `__` continua sendo **sublinhado**, porque é o que a barra escreve desde o
  ADR-024. Trocar para negrito mudaria o desenho das anotações que já existem.
- a quebra de linha simples continua sendo quebra. No CommonMark ela vira
  espaço, mas as anotações foram escritas contando com ela;
- HTML embutido aparece como texto.

**Um leitor só.** A pré-visualização e a tarefa concluída usam o mesmo
`MarkdownView`. Dois leitores desenhariam a mesma anotação de dois jeitos. Editor
e leitor ficam numa `UniformGrid` de uma linha: o que está escondido não ocupa
coluna. Assim cada modo usa a largura inteira, e "lado a lado" divide ao meio.
Nesse modo a coluna da janela alarga de 920 para 1680px, só enquanto a aba da
anotação está à vista. Os botões de formatar somem em "Visualizar": não há
seleção para formatar.

**Link abre com clique simples**, como no preview. Ninguém mais sabe onde cada
`Run` caiu, então o clique acha o link pela posição no `TextLayout`
(`HitTestPoint`). As posições são contadas à mão, e o `LineBreak` conta como
`Environment.NewLine`. Um teste confere isso contra o layout de verdade. Só
abrem `http`, `https` e `mailto`: um `file:` numa anotação não deve virar
execução de programa.

**Cores de apoio vêm do texto.** O fundo do código, as bordas da tabela e a
linha horizontal são o `Foreground` com pouca opacidade. Assim servem em
qualquer fundo. Link e barra da citação chegam por propriedade
(`LinkForeground`, `AccentBrush`), com os recursos do tema.

**Limites aceitos:** o leitor não sincroniza a rolagem com o editor no modo
lado a lado. Imagem não é carregada. Link por referência (`[x][ref]`) e nota de
rodapé aparecem como texto. Bloco de código recuado com quatro espaços não é
código: esse recuo já significa sublista, e é o mais comum numa anotação.
