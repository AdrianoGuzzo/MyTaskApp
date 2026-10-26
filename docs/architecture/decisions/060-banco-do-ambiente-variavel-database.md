# ADR-060 — Banco do ambiente: a cópia na aba Desenvolvimento e o `{database}` dos comandos

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | banco-de-dados |
| **Registrado** | 2026-10-13, commit `2a6da55` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | TaskDevelopmentDatabase, {database}, CommandContext, DatabaseAliasVariable, CopyDevelopmentDatabaseHandler, pós-Worktree, PlaceholderCompletionViewModel, apelido de banco |
| **Relações** | revisa: [ADR-051](051-comandos-rapidos-no-worktree.md); relacionado: [ADR-027](027-aba-desenvolvimento-worktree-git.md), [ADR-056](056-operacoes-de-banco-politica-central.md), [ADR-057](057-conexao-por-servidor-e-apelidos.md), [ADR-058](058-anonimizar-na-consulta.md), [ADR-059](059-servidor-mcp-local-loopback.md), [ADR-065](065-limpeza-de-worktrees-com-forca.md), [ADR-081](081-consulta-inteligente-somente-leitura.md), [ADR-090](090-copia-de-banco-remota-com-aprovacao.md) |
| **Código** | `src/MyTaskApp.Domain/Tasks/TaskDevelopmentDatabase.cs`, `src/MyTaskApp.Application/Development/DevelopmentDatabase.cs`, `src/MyTaskApp.Application/Commands/CommandContext.cs`, `src/MyTaskApp.Desktop/ViewModels/PlaceholderCompletionViewModel.cs` |

**Contexto:** a cópia de banco (ADR-056/057/058) só existia na janela Bancos
de Dados. O usuário quer:

- copiar o banco da própria aba Desenvolvimento;
- já ao criar o worktree, para o ambiente nascer pronto, com o seu banco;
- passar o nome criado aos comandos seguintes — `configura-ambiente {nomeDoBanco}`.

O nome é dinâmico: `{prefixo}_aaaaMMdd_HHmmss` (ADR-057), conhecido só no fim
da cópia.

**Decisão:** o ambiente (`TaskDevelopment`) guarda o banco da última cópia, e
os comandos o recebem em variáveis de contexto: `{database}` (=
`{database.name}`), `{database.host}`, `{database.port}` e `{database.user}`.

## O retrato, e não a conexão

`TaskDevelopmentDatabase` é owned e opcional em `TaskDevelopments` (migration
`DevelopmentDatabase`, só aditiva). Guarda nome, host, porta, usuário, o nome
e o id da conexão de destino, a auditoria e quando. É uma **cópia**, pela mesma
razão dos caminhos no ADR-027: a conexão pode ser editada ou apagada, e o banco
continua onde foi criado. O id só sugere o destino de "Copiar de novo".

- **A última cópia vence.** Copiar de novo troca o retrato; o banco anterior
  fica no servidor.
- **Nunca apaga.** Remover o worktree não apaga o banco, e a confirmação diz
  qual banco fica. O retrato continua no ambiente removido. Tentar de novo no
  mesmo ambiente (outro `git worktree add`) começa sem banco.
- **Nunca a senha.** Ela fica no cofre, com a conexão. Não há
  `{database.password}`: quem monta connection string usa o pgpass ou a
  variável de ambiente que já usa.

## A ordem: validar, Git, copiar, comandos

1. "Iniciar implementação" confere os comandos e, com "Copiar banco para este
   ambiente" marcado, **valida a cópia e pede a confirmação de produção antes
   do Git** (`ValidateDevelopmentDatabaseCopyHandler`, que também recusa com
   outra operação de banco em andamento). Recusar não cria nada.
2. O worktree é criado como sempre, e o painel já fica Pronto: o worktree
   serve sem o banco.
3. A cópia roda (`CopyDevelopmentDatabaseHandler`), com barra, etapa, Cancelar
   e logs no card do banco, dentro do card Pronto.
4. Só com a cópia bem-sucedida os comandos pós-Worktree rodam, já com
   `{database}`. Se ela falha ou é cancelada, nada é desfeito, e a mensagem
   manda copiar de novo e depois "Executar comandos".

O caso de uso só envolve o `RunDatabaseCopyHandler`. Política, confirmação
digitada de produção crítica, a porta de uma operação por vez e auditoria
ficam onde estão. Ele confere o ambiente antes da cópia e grava o nome gerado
depois. Gravar é com `CancellationToken.None`, e sem a guarda da lista
principal, porque o banco já existe no servidor.

## O que copiar

A escolha usa os cadastros da janela Bancos de Dados, lidos só quando ela abre:

- **Apelido + servidor.** O destino só pode ser uma conexão **sem banco
  fixo**, fora de produção e ativa. Cada ambiente ganha um banco novo, sem
  `dropdb`. A anonimização vem do apelido e pode ser trocada. Não recria o
  destino nem guarda o dump.
- **Perfil de cópia.** As opções são as do perfil. Se o destino tem banco
  fixo e "Recriar o destino", a prévia avisa que ele será apagado.
  - Com a origem só de servidor, o banco vem do perfil e pode ser trocado,
    como na aba Copiar Banco.
  - "Salvar como" grava origem, banco e anonimização como apelido
    (`SaveSavedDatabaseHandler`), para repetir pelo modo apelido.
  - Havendo apelido para a origem e o banco escolhidos, o pedido leva o
    `SavedDatabaseId`, e o banco novo ganha o nome do apelido.

Não há cópia padrão por diretório: o usuário escolhe a cada ambiente.

## As variáveis do banco

- **Sem banco copiado, a variável é recusada com o motivo.** O
  `CommandContext` ganhou um "indisponível com motivo" ("este ambiente não tem
  banco copiado…"). O comando para antes do shell, inclusive na forma com
  ponto. Isso também fechou uma brecha antiga: `{repository.name}` sem contexto
  ia cru ao shell, porque `CommandParameters.Names` não enxerga ponto. Agora
  falta como qualquer parâmetro.
- **No valor de um parâmetro.** `@configura-ambiente nomebanco={database}`
  passa o banco a um comando pronto que já tem parâmetro. O valor explícito
  tem as variáveis de contexto preenchidas antes de entrar no comando, ainda
  numa passada só. Na janela de parâmetros do comando rápido vale o mesmo,
  inclusive como valor padrão: `{database}`.
- **Na entrada literal.** `configura-ambiente {database}` direto na lista
  pós-Worktree funciona, porque a entrada literal passou a ter só as variáveis
  de contexto preenchidas (era intocada no ADR-051). Parâmetro do usuário,
  `{ $_ }`, `HEAD@{1}` e `${env:X}` seguem como escritos: não são nome de
  variável.
- **Antes do worktree.** `ValidateCommandEntries` ganhou `DatabaseScheduled`.
  Com a cópia marcada, `{database…}` fica adiado como `{worktree}`. Sem ela, é
  recusado já no "Iniciar implementação".
- **"Testar"**, na janela de globais, não tem ambiente. A mensagem sugere
  passar `database=meu_banco`.
- **Compatibilidade.** Um global antigo com um parâmetro `{database}` deixa de
  pedir o valor na janela do comando rápido, porque o nome passou a ser de
  contexto. `@x database=valor` continua vencendo, como todo valor explícito.
- **A dica da lista pós-Worktree** não pede mais `worktree=…` nem
  `database=…`: as variáveis de contexto ficam no texto, como na execução.

## `{apelido}` e o autocompletar com `{`

O nome do apelido de banco também é variável: `{lock_eco_core_1010}` vale o
banco copiado quando a cópia do ambiente veio daquele apelido. O retrato do
banco guarda o apelido (`Database_Alias`, migration `DevelopmentDatabaseAlias`).
Continua um banco por ambiente.

- **Variável de runtime.** Os apelidos só existem no cadastro, então o
  `CommandContext` ganhou `DatabaseAliasVariable`: nomes fora da lista fixa,
  com valor, adiados ou indisponíveis com motivo.
  - O apelido da cópia vale o nome do banco, mesmo que tenha saído do cadastro.
  - Os outros dizem de onde o banco veio ("veio do apelido X", "não veio de
    um apelido", "não tem banco copiado").
  - Uma variável fixa com o mesmo nome (`database`, `branch`…) vence.
  - O resolvedor passou a olhar `context.Knows(nome)`, e não só a lista fixa.
    Por isso o apelido tem a mesma conferência de motivo e de caractere
    perigoso, na entrada literal, no `@global` e no valor explícito.
- **Nome de apelido nunca é parâmetro.** O editor de comandos globais grava
  uma definição para todo `{nome}`, então "o parâmetro declarado vence" não
  funcionaria.
  - `ParametersOf` exclui os apelidos, e o comando rápido não pede o valor.
  - O editor não cria a linha de parâmetro.
  - Só o `nome=valor` explícito vence.
  - Um parâmetro antigo com o nome de um apelido criado depois passa a ser
    preenchido pelo ambiente.
- **Antes do worktree,** `ValidateCommandEntries` recebe o apelido da cópia
  marcada (o apelido escolhido, ou o do perfil, se existir). Ele fica adiado
  como `{database}`, e os outros são recusados com o motivo.
- **O `{`.** `AliasCompletion.FindToken` ganhou o gatilho.
  - O `{` vale em qualquer lugar da linha, menos depois de `$` e `@`
    (`${env:X}`, `HEAD@{1}`).
  - Aceitar engole o `}` que já estava depois do cursor.
  - A fonte é `PlaceholderCompletionViewModel`, com a lista de
    `CommandPlaceholders`: os apelidos primeiro, depois as variáveis.
  - Na lista pós-Worktree é um segundo popup na mesma caixa do `@`. Na
    janela de parâmetros é o `ParameterValueBox`, com o valor de cada
    variável neste ambiente.

## Fechar a janela e o app

- A janela da tarefa **não fecha** com cópia em andamento. Ela mostra a aba e
  pede para aguardar ou cancelar. Não cancela sozinha, porque cancelar deixa o
  destino pela metade: é a regra da criação do worktree, e não a dos comandos.
- Fechando o app, `DatabaseCopyRuns` cancela as cópias da aba, como a janela
  Bancos de Dados já fazia com a dela. A limpeza do diretório temporário roda
  no caminho de volta.

O MCP (`worktree_list`) mostra o nome e o servidor do banco de cada ambiente,
sem o usuário. Continua sem copiar (ADR-059).

**Testes:**

- **Domain:** Ready obrigatório, a última vence, remover mantém, tentar de
  novo limpa, arquivada grava o que terminou, validações.
- **Application:**
  - o handler sobre o cenário de cópia real com fakes: nome gerado, falha que
    mantém o anterior, produção sem confirmação, porta ocupada;
  - contexto e resolvedor: indisponível com motivo, valor explícito, variável
    no valor e na entrada literal;
  - a validação com e sem cópia e o comando rápido.
- **Infrastructure:** ida e volta do owned e a troca pela próxima cópia.
- **Desktop:**
  - a ordem validar → Git → copiar → comandos;
  - recusar a confirmação não cria nada;
  - a falha da cópia segura os comandos;
  - a escolha (destinos, apelido, perfil);
  - a janela headless.
- **MCP:** `worktree_list` com o banco e sem o usuário.
