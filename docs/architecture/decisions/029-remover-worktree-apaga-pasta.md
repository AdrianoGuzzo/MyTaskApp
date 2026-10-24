# ADR-029 — Remover o worktree apaga a pasta, e mostra quem a segura

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | git-worktree |
| **Registrado** | 2026-09-23, commit `2a3ae2c` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | IDirectoryRemover, WindowsDirectoryLockFinder, Restart Manager, NtQuerySystemInformation, IsLeftover, Encerrar processos e remover, git worktree remove |
| **Relações** | relacionado: [ADR-027](027-aba-desenvolvimento-worktree-git.md), [ADR-031](031-varios-ambientes-por-tarefa.md), [ADR-034](034-bolinha-worktree-e-pergunta-ao-concluir.md), [ADR-051](051-comandos-rapidos-no-worktree.md), [ADR-065](065-limpeza-de-worktrees-com-forca.md) |
| **Código** | `src/MyTaskApp.Infrastructure/FileSystem/WindowsDirectoryLockFinder.cs`, `src/MyTaskApp.Infrastructure/FileSystem/FileSystemDirectoryRemover.cs`, `src/MyTaskApp.Application/Abstractions/IDirectoryRemover.cs`, `src/MyTaskApp.Application/Development/RemoveWorktree.cs` |

**Contexto:** com um terminal, a IDE ou um executável aberto dentro do
worktree, o `git worktree remove` apaga os arquivos, **esquece o worktree**
(ele some da `worktree list`) e só então sai com erro 255
(`failed to delete '…': Permission denied`), deixando as pastas vazias. O app
tratava o exit code como "o Git não removeu", e a tarefa ficava presa: a pasta
existia, mas o Git já não a reconhecia.

**Decisão:**

- Depois de uma falha do `worktree remove`, a pergunta é **"o Git ainda lista
  o worktree?"**, e não o exit code. Se lista, é recusa de verdade (worktree
  trancado, alterações) e nada muda. Se não lista, só falta a pasta.
- A pasta que sobrou é apagada por `IDirectoryRemover` (Application,
  implementado em `Infrastructure/FileSystem`). É a única porta do app que
  apaga pasta. Ela tenta algumas vezes com pausa curta (antivírus e indexador
  soltam logo), tira o somente-leitura sem entrar em junction nem symlink e
  usa `Directory.Delete` recursivo.
- Uma pasta que existe e o Git não conhece é tratada como **sobra**: a
  inspeção devolve `IsLeftover`, a confirmação diz que o Git já a esqueceu, e o
  caso de uso apaga direto. A exceção é a pasta ter virado um repositório
  próprio (`rev-parse --show-toplevel` igual ao caminho): aí não é mais o
  worktree da tarefa, e nada é apagado.
- **Quem segura a pasta** (`WindowsDirectoryLockFinder`), por duas perguntas,
  porque cada uma vê o que a outra não vê:
  - **Restart Manager** sobre os arquivos que sobraram (no máximo 1 000): acha
    executável rodando dali e DLL carregada dali, que são imagem mapeada, e não
    handle aberto;
  - **tabela de handles do sistema** (`NtQuerySystemInformation`, classe 64),
    filtrada pelo tipo "File" (o índice sai de um handle que o próprio app abre
    na pasta, porque muda entre versões do Windows). Cada handle é duplicado e,
    só se `GetFileType` disser disco, o caminho é lido com
    `GetFinalPathNameByHandle` e comparado com o da pasta. Acha o terminal cuja
    pasta de trabalho está lá dentro, que é o caso mais comum, e que o Restart
    Manager não enxerga, porque ele só aceita arquivo.
- **Forçar é um segundo clique, sobre a lista que o usuário viu.** A primeira
  tentativa nunca encerra nada: a tela mostra nome, PID e executável de cada
  processo e oferece "Encerrar processos e remover". O comando leva essa lista,
  e o removedor só encerra quem nela ainda estiver segurando a pasta, conferindo
  PID **e** nome (PID é reaproveitado). Processo que apareceu depois volta para
  a tela. Encerra só o processo, não a árvore: um filho com a pasta aberta
  aparece na lista por conta própria.
- **Nunca encerrados:** o próprio app e o `explorer`. Aparecem na lista com
  "não será encerrado"; se só eles seguram, o botão de forçar não aparece.
- A tarefa só vira `Removed` quando a pasta sumiu de fato. Cada processo
  encerrado vai para o log (`WorktreeLockerTerminated`).

**Armadilhas:**

- **Pipe síncrono trava** quem pergunta o nome dele. Por isso o caminho só é
  lido de handle de disco, e a varredura roda numa thread própria com prazo de
  10 s. Uma exceção nessa thread é capturada: solta, derrubaria o app.
- `FILETIME` dentro de `RM_UNIQUE_PROCESS` tem alinhamento 4: são dois `uint`,
  e não um `long`, senão o struct desalinha.
- Só dá para duplicar handle de processo do mesmo usuário e não elevado. Um
  processo de administrador segurando a pasta não aparece, e a tela diz que o
  Windows não informou quem é.

**Limites aceitos:**

- só Windows. Fora dele a pasta aberta não impede apagar, e o localizador
  devolve lista vazia;
- encerrar é `Kill`: o que não estiver salvo no processo se perde, e a tela
  avisa disso antes do clique.
