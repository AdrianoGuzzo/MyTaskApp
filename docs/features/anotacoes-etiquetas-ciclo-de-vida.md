# Anotações, etiquetas e ciclo de vida

| | |
|---|---|
| **Status** | Implementado. O corretor ortográfico só existe no Windows. |
| **Decisões** | [ADR-024](../architecture/decisions/024-anotacao-markdown-proprio.md) e [ADR-038](../architecture/decisions/038-anotacao-markdown-vscode-e-previa.md) anotação Markdown · [ADR-032](../architecture/decisions/032-correcao-ortografica-do-sistema.md) corretor · [ADR-025](../architecture/decisions/025-etiquetas-n-n-bolinhas.md) etiquetas · [ADR-026](../architecture/decisions/026-diretorios-da-etiqueta-alias.md) diretórios · [ADR-064](../architecture/decisions/064-repositorio-git-do-diretorio-clone.md) repositório do diretório · [ADR-077](../architecture/decisions/077-importar-etiqueta-de-outro-computador.md) importar etiqueta · [ADR-020](../architecture/decisions/020-ciclo-de-vida-estado-derivado.md) ciclo de vida · [ADR-021](../architecture/decisions/021-agendador-de-manutencao-do-ciclo-de-vida.md) manutenção automática |
| **Código** | `src/MyTaskApp.Desktop/Notes/`, `src/MyTaskApp.Desktop/SpellChecking/`, `src/MyTaskApp.Domain/Tags/`, `src/MyTaskApp.Application/Tags/`, `src/MyTaskApp.Application/Lifecycle/`, `src/MyTaskApp.Desktop/ViewModels/TaskNotesViewModel.cs`, `src/MyTaskApp.Desktop/Views/TagsWindow.axaml`, `src/MyTaskApp.Desktop/Views/DataManagementWindow.axaml` |
| **Testes** | `tests/MyTaskApp.Desktop.Tests/Notes/`, `tests/MyTaskApp.Application.Tests/Tags/`, `tests/MyTaskApp.Application.Tests/Lifecycle/` |

## Anotação

- **Anotação em Markdown** por tarefa, com barra de formatação e os modos
  **Escrever**, **Visualizar** e **Lado a lado** (`Ctrl+Shift+V`, como no VS
  Code). A renderização segue o preview do VS Code: títulos, listas aninhadas,
  `- [ ]`, código, citação, tabela e links.
- **Correção ortográfica** com o corretor do Windows: sublinhado vermelho e
  sugestões no clique direito.

## Etiquetas e diretórios

- **Etiquetas** com nome e cor, quantas quiser por tarefa. Na linha elas
  aparecem como bolinhas coloridas.
- **Diretórios da etiqueta:** um `@alias` aponta para uma pasta, que pode ter
  uma branch de origem padrão. Digitar `@` na anotação troca o alias pelo
  caminho real.
- **Repositório Git do diretório:** com o Git instalado, cole a URL do
  repositório e clique em **Clonar**. A pasta já vem sugerida, ao lado dos
  outros projetos, e o diretório é salvo junto. Numa pasta que já é
  repositório, a URL acompanha o `origin` dela.
- Com a sincronização, o caminho da pasta não viaja: o diretório chega "não
  mapeado neste computador", com a URL para clonar aqui. Uma etiqueta de outro
  computador pode ser importada pela janela de etiquetas
  ([sincronização](../sync.md#onde-cada-tarefa-aparece)).

## Arquivo, lixeira e auditoria

- **Arquivar e mover para a lixeira** pelo menu `⋯` da linha. Em
  *☰ → Gerenciamento de dados…* ficam as abas Concluídos, Arquivados e Lixeira,
  com restauração, exclusão definitiva e histórico de auditoria; a política de
  arquivamento automático fica na aba Configurações. A aba Worktrees limpa os
  worktrees que sobraram ([desenvolvimento](desenvolvimento-worktrees.md)).
- O arquivamento automático nasce desligado, e a auditoria sobrevive à exclusão
  definitiva (sem chave estrangeira), como decidido no ADR-020.
