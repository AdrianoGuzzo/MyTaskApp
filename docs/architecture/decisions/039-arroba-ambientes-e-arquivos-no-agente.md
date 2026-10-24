# ADR-039 — `@` no texto do agente: os ambientes da tarefa e os arquivos deles

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | agente-ia |
| **Registrado** | 2026-09-26, commit `0b01650` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | @, ListEnvironmentFiles, PathReference, ReferenceCompletionViewModel, git ls-files, busca fuzzy, IAliasCompletionSource, ContinuationFor |
| **Relações** | relacionado: [ADR-026](026-diretorios-da-etiqueta-alias.md), [ADR-030](030-sessoes-claude-code-por-tarefa.md), [ADR-031](031-varios-ambientes-por-tarefa.md), [ADR-033](033-parametros-do-agente-lembrados.md) |
| **Código** | `src/MyTaskApp.Application/Development/DevelopmentQueries.cs`, `src/MyTaskApp.Desktop/Notes/PathReference.cs`, `src/MyTaskApp.Desktop/ViewModels/ReferenceCompletionViewModel.cs`, `src/MyTaskApp.Desktop/Notes/IAliasCompletionSource.cs` |

**Contexto:** com vários ambientes por tarefa (ADR-031), o texto para o agente
(ADR-033) costuma citar outro repositório da mesma tarefa: "olhe o projeto
`C:\Projetos\MyTaskApp-feature-…`". Escrever o caminho do worktree, e mais
ainda o de um arquivo dentro dele, é lento e dá erro.

**Decisão:** na caixa "Texto para o Claude Code", digitar `@` abre uma lista
com os **ambientes prontos da mesma tarefa**, cada um com uma chave curta, o
nome da pasta do repositório (`@MyTaskApp`, `@eco-api`). Enter insere o
caminho absoluto do worktree. Tab (ou digitar `/`) entra nele: `@MyTaskApp/`
lista a raiz, e o que vem depois da barra procura dentro. Numa pasta, Tab entra
de novo e Enter insere a pasta. Num arquivo, os dois inserem o caminho do
arquivo. Com duas letras ou mais, `@texto` sem ambiente procura nos arquivos de
todos os ambientes, e cada item mostra de qual é.

**Só a mesma tarefa.** A lista vem dos ambientes gravados na tarefa, e o caso de
uso `ListEnvironmentFiles(TaskId, DevelopmentId)` tira o ambiente **da
tarefa** (`GetDevelopment`). Um id de outra tarefa é recusado como ambiente
inexistente. Ambiente sem worktree (Erro, Removido, Criando) não entra: não há
pasta para citar.

**O texto guarda o caminho, e não a chave.** É a regra do ADR-026: quem lê o
texto é o agente, e ele entende caminho, não apelido do app. Remover o worktree
depois não reescreve textos antigos.

**A lista de arquivos vem do Git**: `git ls-files --cached --others
--exclude-standard -z`. Entram os arquivos commitados e os novos, e fica de
fora o que o `.gitignore` esconde (`bin`, `obj`, `node_modules`). Varrer a pasta
traria dezenas de milhares de arquivos gerados. As pastas saem dos caminhos dos
arquivos, porque o Git não lista pasta. O teto é de 50 000 arquivos
(`ListEnvironmentFilesHandler.MaxFiles`), e a lista avisa quando corta.

**Quando busca.** Só quando um `@` abre, e de novo a cada `@` novo, em segundo
plano: o agente cria arquivos enquanto o usuário escreve. A lista velha vale até
a nova chegar. Sem lista ainda, o popup espera aberto com "Carregando os
arquivos…". Falhar só vai para o log e fecha a lista.

**Busca por trecho, no estilo do Ctrl+P** (`Notes/PathReference`, puro e
testado sem janela). As letras precisam aparecer na ordem, não juntas:
`tdvm` acha `TaskDevelopmentViewModel.cs`. O que casa só no nome do arquivo
ganha do que precisa da pasta, e começar o nome com o digitado ganha mais. O
nome exato vem primeiro. Começo de palavra (depois de `/ . - _`, ou a maiúscula
do camelCase) e letras seguidas somam, e cada salto desconta. O casamento é o
**melhor**, e não o primeiro: com o guloso, `tdvm` pegava o `v` de
"De**v**elopment" e perdia para `TodayViewModel`. É programação dinâmica em
duas linhas, O(texto × busca), com as linhas na pilha. Antes dela vem a
conferência barata de que as letras existem na ordem. Medido num caso
sintético de 50 000 arquivos em pastas aleatórias (200 000 entradas): 20 a 55 ms
por tecla. Um repositório de verdade tem muito menos pastas.

**Pasta conhecida delimita.** Em `@MyTaskApp/src/Views/tod`, se `src/Views`
existe, a busca é só dentro dela. Se não existe, a busca vale para o caminho
inteiro, e `views/tod` também acha `src/…/Views/TodayView.axaml`.

**O mesmo binder, com dois ganchos.** `IAliasCompletionSource` ganhou dois
membros com implementação padrão. `ContinuationFor` diz o que o Tab põe no
lugar do `@texto` sem fechar a lista (entrar na pasta), e `null` mantém o Tab
como Enter. `IsTokenChar` diz até onde vai o token, que aqui inclui a barra.
As fontes de antes (diretórios das etiquetas, comandos) não mudaram.
`AliasCompletion.FindToken` e `Accept` ganharam uma sobrecarga com o predicado.

**Uma instância para a aba.** `ReferenceCompletionViewModel` mora em
`TaskDevelopmentsViewModel`, como `DirectoryCompletion`, e chega ao card do
agente por `AgentSessionViewModel.References`. A caixa é a do ambiente da
frente, e ele aparece marcado "este ambiente". A lista é a mesma para todas as
abas.

**Limites aceitos:**

- nome de arquivo com espaço não é achado depois de um espaço, porque o
  espaço encerra o token. A busca por trecho quase sempre acha sem ele;
- o caminho inserido não leva aspas. O `claude.cmd` do npm recusa `"` no
  texto (ADR-030), e o agente lê o caminho do jeito que está;
- ambientes de **outras** tarefas não aparecem. É a regra pedida, e não um
  esquecimento.
