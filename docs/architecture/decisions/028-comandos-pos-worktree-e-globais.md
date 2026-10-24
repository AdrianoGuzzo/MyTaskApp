# ADR-028 — Comandos pós-Worktree e comandos globais por `@alias`

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | git-worktree |
| **Registrado** | 2026-09-23, commit `4c66a6b` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | TaskDevelopmentCommands, DevelopmentCommands, CommandAliasResolver, ShellCommandExecutor, CommandParameters, pós-Worktree, @alias, chcp 65001 |
| **Relações** | relacionado: [ADR-026](026-diretorios-da-etiqueta-alias.md), [ADR-027](027-aba-desenvolvimento-worktree-git.md), [ADR-051](051-comandos-rapidos-no-worktree.md), [ADR-055](055-comando-so-do-diretorio.md), [ADR-088](088-catalogo-fechado-de-operacoes-remotas.md) |
| **Código** | `src/MyTaskApp.Application/Commands/CommandAliasResolver.cs`, `src/MyTaskApp.Infrastructure/Processes/ShellCommandExecutor.cs`, `src/MyTaskApp.Infrastructure/Processes/ShellCommandPlanner.cs`, `src/MyTaskApp.Domain/Commands/DevelopmentCommand.cs` |

**Decisão:** o ambiente de desenvolvimento (ADR-027) ganha uma **lista ordenada
de comandos** (`TaskDevelopmentCommands`, 1:N com `TaskDevelopments`, coluna
`Order`). Quando o worktree fica pronto, a aba roda a lista **em sequência**, com
o diretório do worktree como pasta de trabalho, mostra o output ao vivo e para no
primeiro comando que não der certo. Os seguintes ficam "não executado". Também há
uma biblioteca de **comandos globais** (`DevelopmentCommands`): um apelido único
(`@restore`) que aponta para uma linha (`dotnet restore`). Ela é administrada
numa janela própria e oferecida por autocomplete ao digitar `@`.

**Um comando por linha, e não um texto com `&&`.** Assim há status, output e
falha por etapa, e a parada na primeira falha é regra do app, não do shell.
Quebra de linha dentro de um comando é recusada no domínio.

**O alias de comando é referência, não atalho.** Aqui o contrário do ADR-026: a
tarefa guarda `@restore` como foi digitado, e a resolução acontece **na hora de
executar** (`CommandAliasResolver`, puro). Editar o global vale para todas as
tarefas. Só o primeiro termo é olhado: `@build -c Release` vira
`dotnet build -c Release`. Global não chama global, então não há recursão. Um
termo com forma de alias que não existe é **erro** da etapa, e não vai ao shell
como literal. Antes de criar o worktree, "Iniciar implementação" confere todos os
apelidos (`ValidateCommandEntries`) e recusa com a lista dos que faltam: é melhor
do que descobrir com o ambiente já criado. A regra de forma do alias
(`AliasRule`) é a mesma dos diretórios, e o autocomplete reaproveita
`AliasCompletion` e o `AliasCompletionBinder`, agora sobre a interface
`IAliasCompletionSource`. Diferenças do lado do comando: a lista só abre no
**primeiro** termo, e aceitar insere o apelido, e não o comando.

**Parâmetros.** O comando global pode ter `{nome}` (`CommandParameters`, no
domínio): `eco-sync {nomebanco} -Dev`. Quem chama preenche na própria linha,
`@eco-sync nomebanco=MeuBanco`, e o valor fica gravado com a tarefa. A lista
roda sem perguntar nada. O valor entra como foi escrito, com as aspas. O que
não é `nome=valor` de um parâmetro continua sendo acrescentado no fim. O nome
começa com letra ou sublinhado e não tem espaço, então `{ $_ }`, `${env:X}` e
`HEAD@{1}` continuam literais. Parâmetro sem valor é erro da etapa, como alias
que não existe: `ValidateCommandEntries` recusa antes de criar o worktree, e o
shell nunca recebe `{nome}`. Nada muda no banco, porque os parâmetros saem do
texto do comando. A janela de globais explica a sintaxe, mostra os parâmetros
enquanto o usuário digita e mostra o "Uso: @alias nome=…" na lista. "Testar"
tem um campo para os valores. O autocomplete insere `@alias nome=`.

**Quando roda.** Só depois de `StartDevelopment` terminar com o ambiente
`Ready`. Isso vale para a criação, para "usar Worktree existente" e para
"caminho alternativo". Falha na criação retorna antes. Rodar de novo é pelo botão
"Executar comandos", com o ambiente pronto, e grava a lista antes se ela foi
alterada. "Testar", na janela de globais, roda numa pasta escolhida. Nada roda
sem uma dessas ações explícitas. `RunDevelopmentCommands` confere o status e a
existência da pasta antes da primeira etapa.

**Execução (`ICommandExecutor` → `ShellCommandExecutor`):**

- **Windows:** `%ComSpec% /d /s /c "chcp 65001>nul & <linha>"`, com a linha crua
  em `Arguments`. É a receita de Node/libuv: `/s` tira só as aspas externas e
  preserva aspas internas, `&&` e pipes. É o cmd, e não o PowerShell 5.1, porque
  o 5.1 não conhece `&&`. O `chcp 65001` põe o console escondido do processo em
  UTF-8. Sem isso, o output chega na página OEM, com acento quebrado. `&` tem a
  menor precedência, então o exit code é o da linha do usuário.
- **Linux/macOS:** `/bin/sh -c <linha>`, com a linha como **um** argumento de
  `ArgumentList`. Nada é concatenado nem escapado pelo app.
- stdin fechado logo no início, porque um prompt interativo receberia EOF em vez
  de travar. stdout e stderr são lidos linha a linha, ao mesmo tempo, e cada
  linha vai ao `IProgress` marcada com o stream. O texto guardado tem teto de
  1 MB por stream.
- Cancelar ou estourar o timeout (padrão de 30 min) faz
  `Kill(entireProcessTree: true)` e devolve **resultado** (`Canceled` ou
  `TimedOut`), com o output de até ali. Só a falha em iniciar o shell vira
  exceção (`CommandStartException`). Exit code diferente de zero é resultado,
  não erro.
- Depois do exit, o executor espera no máximo 5 s pelo fim dos pipes: um
  processo deixado em segundo plano herda a saída e a seguraria aberta para
  sempre.

**Thread de UI.** As linhas saem da thread que lê o pipe (`ConfigureAwait(false)`
de propósito). `UiProgress<T>` posta essas linhas no contexto da UI e aplica na
hora o que já chega nele (começou/terminou). O resumo final do caso de uso é a
fonte da verdade dos status, porque uma linha atrasada só acrescenta texto à
etapa dela. O terminal (`CommandOutputView`) é uma `ListBox` virtualizada com
altura fixa e as últimas 5 000 linhas, e segue o fim a menos que o usuário
tenha subido.

**Segurança.** O output não vai para o log, porque pode ter segredo. Vão o
comando, a etapa e o exit code. Fechar a janela com comandos rodando cancela
(mata a árvore) em vez de deixar processos órfãos. O primeiro "X" cancela e
mostra o que houve, e o segundo fecha.

**Limites aceitos:**

- sem "continuar mesmo com falha" por etapa, porque a regra é fixa nesta versão;
- o resultado da execução não é gravado: sobrevive enquanto a janela está aberta;
- sem terminal interativo, já que stdin está fechado;
- sem variáveis de ambiente por comando.

## Divergências com o código (conferido em 2026-10-24)

- O ADR fala em teto de 1 MB por stream; o código limita em caracteres, MaxCapturedChars = 1_000_000 (src/MyTaskApp.Infrastructure/Processes/ShellCommandExecutor.cs).
