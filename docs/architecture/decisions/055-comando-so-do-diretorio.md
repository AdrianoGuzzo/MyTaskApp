# ADR-055 — Comando só do diretório: o botão sem passar por Comandos globais

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | git-worktree |
| **Registrado** | 2026-10-08, commit `e92c80e` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | DevelopmentCommand.TagDirectoryId, CreateForDirectory, DirectoryCommandWindow, comando do diretório, Alias anulável, Novo comando |
| **Relações** | revisa: [ADR-051](051-comandos-rapidos-no-worktree.md); relacionado: [ADR-028](028-comandos-pos-worktree-e-globais.md), [ADR-066](066-comandos-globais-locais-por-padrao.md) |
| **Código** | `src/MyTaskApp.Domain/Commands/DevelopmentCommand.cs`, `src/MyTaskApp.Infrastructure/Persistence/Repositories/DevelopmentCommandRepository.cs`, `src/MyTaskApp.Application/Tags/TagDirectoryCommands.cs`, `src/MyTaskApp.Desktop/Views/DirectoryCommandWindow.axaml` |

**Contexto:** no ADR-051, todo botão "▶" do worktree nasce de um comando
global, que depois é ligado ao diretório da etiqueta. Para um comando que só faz
sentido num repositório (`npm run dev` do front, o `docker compose up` de um
serviço só), isso obrigava a cadastrar um global, inventar um `@apelido` que
ninguém vai chamar, e conviver com ele na janela de globais, no autocomplete
dos pós-Worktree e no "+ Adicionar comando" de todos os outros diretórios. O
pedido: manter os globais, e permitir criar o comando direto no diretório.

**Decisão:** o comando só do diretório é o mesmo `DevelopmentCommand`, com um
dono: `TagDirectoryId`. `null` é global, como sempre foi.

| | Global (ADR-028/051) | Só do diretório |
|---|---|---|
| Onde se cria | *Comandos globais…* | *Etiquetas… → Comandos → + Novo comando* |
| Apelido | obrigatório e único | nenhum |
| Nome | opcional (cai no apelido) | obrigatório: é o rótulo do botão |
| `@alias` nos pós-Worktree e "Testar" | sim | não |
| "+ Executar comando…" do card | sim | não: fora do botão, ele não existe |
| Em outros diretórios | sim, com personalização | não |
| Personalizar | override no diretório | edita-se o próprio comando |
| Tirar do diretório (×) | o global continua | exclui o comando, depois de perguntar |

## Um comando, e não um segundo cadastro

Uma tabela própria repetiria o que o comando já tem: modo, pasta, terminal,
confirmação e a definição tipada dos parâmetros (com a tabela de parâmetros
junto), e o catálogo, a preparação, a execução, o "um por vez" e o histórico
teriam de aprender um segundo tipo. Com o dono no próprio comando, tudo isso
fica igual: a associação (`TagDirectoryCommand`) continua dando a ordem e o
ligar/desligar, e a execução continua apontando para um `DevelopmentCommandId`.

- `Alias` passou a ser anulável. O índice único continua: o SQLite não toma
  NULLs repetidos por duplicata. O global ainda o exige (`Create`/`Update`); o do
  diretório nasce sem (`CreateForDirectory`/`UpdateForDirectory`). Cada um
  recusa a edição do outro tipo, com a janela onde ele se edita.
- `IDevelopmentCommandRepository.ListAsync` passou a trazer **só os globais**:
  é o que a janela de globais, o `@alias` e o avulso do card leem.
  `ListForDirectoriesAsync` traz os do diretório.
- O catálogo (`QuickCommandCatalog.Entries`) recebe os globais **e** os comandos
  dos diretórios que casaram com o repositório, e não distingue os dois: a
  associação diz qual é qual. A busca de um botão (`QuickCommandTargets.Find`)
  só cai no avulso para um global.
- `AddTagDirectoryCommand` recusa oferecer o comando de um diretório em outro.

## O dono é o diretório, e não a etiqueta

Pelo mesmo motivo do ADR-051: a etiqueta pode ter vários repositórios. A chave
estrangeira `DevelopmentCommands.TagDirectoryId → TagDirectories` é em cascata:
excluir o diretório, ou a etiqueta, leva os comandos dele (e os parâmetros, pela
cascata que já existia). O histórico perde o vínculo e fica, como na exclusão
de um global. Tirar o botão do diretório (`RemoveTagDirectoryCommand`) exclui
o comando junto. Ele não tem outro lugar onde aparecer, e um comando sem botão
seria lixo invisível.

## Banco

Migration `DirectoryOnlyCommands`: `Alias` anulável e a coluna `TagDirectoryId`,
com índice e chave estrangeira. O SQLite não altera coluna, e o EF reconstrói a
tabela. Um teste sobe de `StickyNotes` com global, parâmetro, botão e histórico,
e confere que nada se solta. O `Down` exclui antes os comandos de diretório,
escrito à mão: sem apelido, eles voltariam com `""` e esbarrariam no índice
único.

## Tela

- **Etiquetas… → Comandos:** o seletor de globais virou "+ Adicionar global", e
  embaixo vem "+ Novo comando", com a dica do que ele faz (ou, sem global a
  oferecer, de onde cadastrar um). O comando do diretório mostra "Só deste
  diretório" e **Editar** no lugar de **Personalizar**. O × dele pergunta antes,
  porque exclui.
- **O diálogo** (`DirectoryCommandWindow`, porta `IDirectoryCommandEditor`) é o
  formulário de Comandos globais sem o apelido: nome, comando, tipo, pasta,
  terminal, confirmação, parâmetros e descrição. Segue o molde do ADR-052: o
  caso de uso grava, e a recusa aparece com o diálogo aberto. A sincronização
  das linhas de parâmetro e a dica das variáveis passaram para
  `DevelopmentCommandParameterEditorViewModel`, usadas pelos dois formulários.
- Excluir um diretório com comandos avisa que os comandos criados só nele saem
  junto.

## Fora de escopo, de propósito

- **Converter** um comando do diretório em global, ou o contrário. Hoje é
  recriar; o dono não muda depois de criado.
- **Comando só da etiqueta**, valendo para todos os diretórios dela: o caso que
  motivou o ADR-051 (`@eco-web` × `@ecossistema-core`) mostra por que a unidade
  é o diretório.
- **Testar** pela janela de etiquetas: o botão no worktree já é o teste.

**Testes:** domínio (dono, sem apelido, nome obrigatório, atomicidade e as
recusas cruzadas de edição), Application (criar no fim da lista, recusa sem
nome e em diretório que não existe sem deixar comando para trás, fora dos
globais, editar e ler, excluir ao tirar do diretório, recusa em outro diretório,
botão no worktree rodando na pasta certa, sem avulso), Infrastructure (ida e
volta sem apelido, dois sem apelido no índice único, cascata do diretório e da
etiqueta, histórico solto e o upgrade a partir de `StickyNotes`) e Desktop (o
diálogo, criar, recusa com o diálogo aberto, cancelar, editar, excluir
perguntando e o global sem perguntar, e os bindings headless da lista e do
diálogo).
