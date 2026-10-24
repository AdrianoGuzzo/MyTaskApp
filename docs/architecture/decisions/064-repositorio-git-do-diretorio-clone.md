# ADR-064 — O repositório Git do diretório: a URL acompanha a pasta, e clonar dali

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | etiquetas |
| **Registrado** | 2026-10-14, commit `34ed1bf` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | RepositoryUrl, GitRemoteUrl, CloneAsync, git clone, origin, SuggestCloneFolder, GitInstallGuide, diretório não mapeado |
| **Relações** | relacionado: [ADR-026](026-diretorios-da-etiqueta-alias.md), [ADR-027](027-aba-desenvolvimento-worktree-git.md), [ADR-047](047-pr-aberta-da-branch-pelo-gh.md), [ADR-061](061-sincronizacao-outbox-mescla-por-campo.md), [ADR-077](077-importar-etiqueta-de-outro-computador.md), [ADR-083](083-diagnostico-instalar-e-banco-docker.md) |
| **Código** | `src/MyTaskApp.Domain/Tags/GitRemoteUrl.cs`, `src/MyTaskApp.Application/Tags/TagDirectoryRepositories.cs`, `src/MyTaskApp.Infrastructure/Git/GitClient.cs`, `src/MyTaskApp.Desktop/ViewModels/TagsViewModel.cs` |

**Contexto:** o diretório da etiqueta (ADR-026) só tinha o caminho. Para
cadastrar um projeto novo, era preciso clonar no terminal, voltar e procurar a
pasta. E o caminho é de cada computador (ADR-061): o diretório chegava no
outro computador "não mapeado", sem nada que dissesse de onde baixá-lo. O
pedido: com o Git disponível, informar o repositório, escolher onde clonar,
com a pasta já sugerida, e, numa pasta que já é repositório, manter a URL
sempre de acordo com ela.

**Decisão:** o diretório ganha `RepositoryUrl`, opcional. No formulário de
*Etiquetas…*, com o Git encontrado, aparece o campo "Repositório Git" acima
do caminho:

- **Colar a URL com o caminho vazio** já escolhe onde clonar:
  `{pasta de clone}\{nome do repositório}`, e o alias junto
  (`@ecossistema-web`). Enquanto o usuário não mexe, corrigir a URL troca a
  sugestão. Um caminho escrito por ele nunca é trocado.
- **A pasta ainda não existe:** aparece "A pasta ainda não existe. Clonar
  baixa o repositório nela", com **Outra pasta…** (escolhe a pasta-mãe, e o
  repositório entra numa subpasta com o nome dele) e **Clonar**. O clone
  termina salvando o diretório: colar a URL e clicar em Clonar já é o
  cadastro inteiro.
- **A pasta já é um repositório:** "Procurar…" preenche a URL pelo `origin`
  dela, e salvar também lê o `origin`. Ao abrir a lista da etiqueta, cada
  pasta encontrada tem a URL conferida, e a que mudou é gravada.
- **O diretório que chegou de outro computador** traz a URL. Editá-lo já
  sugere a pasta e oferece o clone, e a linha diz "edite para clonar ou
  escolher a pasta".

Sem o Git, o campo e o clone não aparecem, e a URL gravada fica como está. No
lugar do campo, um aviso diz que o Git não está instalado e o que se perde
sem ele, com as instruções do ADR-027 (`GitInstallGuide`: o comando do
sistema para copiar, a página oficial) e um "Verificar novamente". Escondida
calada, a URL parecia não existir: quem não tem o Git não tinha como saber
que a função estava ali. O "Verificar novamente" não recarrega a lista: só
atualiza o Git e a pasta de clone dos cartões, e o formulário que o usuário
está preenchendo fica. Como a procura relê o PATH do registro (ADR-027),
basta instalar e clicar, sem reiniciar o app.

## A pasta manda

Com dois campos, um dia eles discordam. Quem vence é a pasta: o `origin` é o
que o Git usa de verdade, e quem trocou o remoto no terminal (um fork, um
servidor novo) espera ver o cadastro acompanhar. O contrário, um
`git remote set-url` a partir do formulário, mexeria na configuração do
repositório do usuário, e o app não altera repositório sem pedido (ADR-027).
Por isso a URL digitada vale para clonar e para pastas que não são
repositório, e é substituída pelo `origin` assim que houver um.

- **Mesma URL escrita de outro jeito não é mudança.** Com ou sem `.git`, com
  barra final ou maiúscula no servidor, é o mesmo repositório
  (`GitRemoteUrl.AreSame`). Sem isso, a lista regravaria e sincronizaria a
  URL a cada abertura.
- **Um `origin` que sumiu não apaga a URL.** A última conhecida ainda serve
  para clonar em outro computador.
- **Só o `origin`.** É o remoto que o clone cria, e é o mesmo que a PR aberta
  já usa (ADR-047).

## A URL viaja, o caminho não

O caminho continua de cada computador. A URL vale em qualquer um, e por isso
entra na sincronização como campo do diretório (`repositoryUrl`). É ela que
faz o diretório "não mapeado" virar um clique em vez de uma pesquisa. Um
cliente mais velho ignora o campo (ADR-061), sem mudar a versão do contrato.

**Credenciais nunca entram.** Um `https://token@github.com/…` copiado do
remoto iria para o banco e, pela sincronização, para o servidor. O domínio
tira usuário e senha de `http(s)://` ao normalizar. O usuário do SSH
(`git@…`, `ssh://git@…`) não é credencial e fica.

## Que URL é aceita

As formas que o Git aceita para clonar: `https://`, `http://`, `ssh://`,
`git://`, `file://` e a curta do SSH, `[usuário@]servidor:caminho`. Como no
Git, com `://` é URL e nunca a forma curta (`ftp://x` não vira servidor
"ftp"), e uma letra antes do `:` é unidade do Windows. Recusadas: caminho
local, espaço, mais de 2048 caracteres e qualquer coisa começando com `-`,
que o Git leria como opção. Além disso, o clone passa os dois valores depois
de `--`.

## O clone

- **`IGitClient.CloneAsync`** roda `git clone -- {url} {caminho}` sem pasta de
  trabalho, com 30 minutos de prazo e cancelamento ("Cancelar clone").
- **É o único comando em que o Git Credential Manager pode abrir a janela de
  login.** Os outros rodam com `GCM_INTERACTIVE=Never` porque são de fundo: um
  fetch esperando login travaria até o timeout. O clone foi pedido por um
  clique, e um repositório privado sem login salvo só tem esse caminho.
  `GIT_TERMINAL_PROMPT=0` continua: não há terminal para alguém digitar.
- **Já clonado não é erro.** Se a pasta existe e o `origin` é o mesmo
  repositório, nada é baixado, e o diretório é salvo.
- **As recusas viram frase:** pasta existente e não vazia, login, repositório
  não encontrado, servidor inalcançável, tempo esgotado. O resto mostra a
  última linha do Git. O comando inteiro vai para o log.
- **O caso de uso não grava o diretório.** Ele devolve caminho e URL, e a tela
  salva como sempre. Se salvar falhar (alias repetido), o clone fica e o
  formulário espera a correção. E o app não apaga o que baixou: a pasta é do
  usuário.
- **Cancelar** mata o processo. O Git não tem como limpar a pasta pela metade,
  e a mensagem diz isso e onde ela ficou.

## Onde clonar por padrão

A pasta-mãe mais comum entre os diretórios já mapeados neste computador
(`SuggestCloneFolder`): quem guarda os projetos em `C:\Projetos` clona lá sem
procurar. Sem nenhum diretório, a pasta do usuário. Não há configuração nova:
a sugestão sai do que o usuário já fez, e "Outra pasta…" resolve a exceção.

## Alternativas

- **Um botão "Clonar repositório…" separado, com janela própria.** Mais um
  lugar para cadastrar a mesma coisa. No formulário, a URL e o caminho ficam
  lado a lado, e o diretório que chegou sem pasta já tem a URL ali.
- **Clonar sem salvar, deixando o "Adicionar diretório" para o usuário.** Um
  clique a mais sem decisão nenhuma no meio.
- **Conferir o `origin` a cada tecla do caminho.** Um processo do Git por
  tecla. A conferência acontece ao escolher a pasta, ao salvar e ao abrir a
  lista. Ao digitar, só a existência da pasta é conferida, o que decide se o
  clone é oferecido.

## Consequências

- Abrir a lista de uma etiqueta roda um `git remote get-url` por pasta
  encontrada, depois de a lista aparecer. Sem o Git, nenhum.
- A detecção do Git roda a cada abertura de *Etiquetas…*, como na aba
  Desenvolvimento: instalar o Git com a janela escondida passa a valer na
  próxima abertura, e com ela aberta, no "Verificar novamente".

**Testes:**

- **Domain:** as formas aceitas e recusadas, credenciais tiradas, o nome da
  pasta que o clone cria, a mesma URL escrita de outro jeito e a troca que só
  conta quando muda.
- **Application:** o `origin` lido e normalizado; o clone com os valores
  normalizados, o já clonado, sem Git, as recusas traduzidas; a lista que
  acompanha o `origin` e grava uma vez só; a pasta de clone sugerida.
- **Infrastructure:** os argumentos e o ambiente do `git clone`; a URL indo e
  voltando no SQLite; um clone de verdade de um `origin` local, com a URL lida
  de volta e a recusa de clonar de novo na mesma pasta.
- **Desktop:** a sugestão de pasta e de alias, o caminho do usuário
  respeitado, "Outra pasta…", o clone oferecido só para pasta inexistente,
  nada sem Git além do aviso com o comando de instalação, o "Verificar
  novamente" que libera a URL sem perder o formulário, clonar e salvar, falha e cancelamento, a URL pelo `origin` ao
  procurar e ao salvar, o diretório de outro computador, a lista recarregada
  quando o `origin` mudou e o formulário desenhado com o "Clonar" ligado ou,
  sem Git, com o aviso e os botões ligados.
