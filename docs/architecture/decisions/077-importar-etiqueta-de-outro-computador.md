# ADR-077 — Importar etiqueta de outro computador: a mesma etiqueta, sem o caminho

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | etiquetas |
| **Registrado** | 2026-10-19, commit `cdeed70` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | importar etiqueta, PlaceTag, DevicePlacement.Everywhere, IsVisibleHere, TagsViewModel, Não mapeado neste computador, DeviceView.All |
| **Relações** | relacionado: [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md), [ADR-062](062-lista-por-computador-onde-aparece.md), [ADR-064](064-repositorio-git-do-diretorio-clone.md) |
| **Código** | `src/MyTaskApp.Desktop/ViewModels/TagsViewModel.cs`, `src/MyTaskApp.Desktop/Views/TagsWindow.axaml` |

**Contexto:** com uma lista por computador (ADR-062), a etiqueta criada no
notebook fica escondida no desktop. Quem quer a mesma etiqueta aqui precisava
saber que "criar com o mesmo nome" a compartilha, ou achá-la em "Onde
aparece…", que só lista as etiquetas daqui. O pedido: ao criar uma etiqueta
nova, importar uma de outro computador com o nome, a cor e o repositório, sem
a pasta.

**Decisão:**

1. **Importar é compartilhar.** É a mesma etiqueta, colocada em "todos os
   computadores" (`PlaceTag` com `DevicePlacement.Everywhere`), como já faz o
   `CreateTag` com o nome de uma escondida.
2. **Nada novo no domínio, no sync ou no servidor.** O nome e a cor vêm com a
   etiqueta. Os diretórios vêm com o que já sincroniza: alias, URL do
   repositório, nome, descrição, branch padrão e comandos. O caminho continua
   coluna local (ADR-061/064), e o diretório chega "Não mapeado neste
   computador". O clone e o "escolher a pasta" do ADR-064 resolvem daí.
3. **Na janela Etiquetas, no formulário de etiqueta nova.**
   - **O botão.** "Importar de outro computador…" ao lado do título. Só
     aparece com outro computador na conta, com alguma etiqueta escondida aqui
     e fora da edição.
   - **O menu.** Uma linha por etiqueta, com a bolinha da cor e de onde vem:
     "Backend — Notebook", ou "um computador removido".
   - **A lista.** `GetTags(DeviceView.All)` filtrado por
     `!IsVisibleHere(placement)`. Ler essa lista não pode derrubar a janela:
     se falhar, o botão só some.
   - **Depois de importar.** A etiqueta já tem diretórios? O cartão dela abre
     com eles, para clonar ou escolher a pasta.

## Alternativas

- **Cópia independente**, só com alias e URL nos diretórios. O nome é único na
  conta (índice `NOCASE` local, `UniqueKeyFields` no servidor), então a cópia
  teria de nascer com outro nome. Também pediria um diretório sem caminho
  criado pelo domínio, que hoje só o sync escreve. O usuário preferiu a mesma
  etiqueta.
- **Nome repetido por computador.** Mexe na regra de unicidade, no contrato do
  sync, no servidor e na fusão por nome (ADR-061). É grande demais para um
  atalho.
- **Preencher o formulário e criar pelo "Criar etiqueta".** Daria a impressão
  de que a cor escolhida ali vale, mas o `CreateTag` com o nome de uma
  escondida só a compartilha. Trocar o nome no formulário criaria outra
  etiqueta sem diretórios.

## Consequências

- Cor e diretórios ficam compartilhados: editar num computador muda no outro.
  O caminho de cada um continua só dele.
- Comandos vinculados aos diretórios vêm junto, porque são do diretório.
- Para voltar, "Onde aparece…" manda a etiqueta de novo para o outro
  computador.

**Testes (Desktop):**

- `TagImportViewModelTests`:
  - só as etiquetas de outro computador são oferecidas, com a origem;
  - sem outro computador, nada é oferecido, e a lista é lida uma vez só;
  - a falha ao ler a lista só esconde o botão;
  - a edição esconde o botão;
  - importar coloca a etiqueta em todos e abre os diretórios não mapeados;
  - a falha ao importar aparece na tela;
  - na janela, o botão e o menu ligados ao comando.
