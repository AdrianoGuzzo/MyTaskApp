# ADR-088 — Catálogo fechado de operações: nenhum texto de shell vem de fora

| | |
|---|---|
| **Status** | Proposto |
| **Implementação** | Parcial |
| **Categoria** | remoto |
| **Registrado** | 2026-10-24, commit `36eb99c` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | catálogo de operações, RemoteAllowed, command.run, RequiresConfirmation, RunQuickCommand.Confirmed, UnsafeCharacter, ICommandReviewGate |
| **Relações** | relacionado: [ADR-028](028-comandos-pos-worktree-e-globais.md), [ADR-051](051-comandos-rapidos-no-worktree.md), [ADR-066](066-comandos-globais-locais-por-padrao.md), [ADR-084](084-orquestracao-remota-coordenador-e-agente.md), [ADR-090](090-copia-de-banco-remota-com-aprovacao.md) |
| **Código** | `src/MyTaskApp.Remote.Contracts/Operations.cs`, `src/MyTaskApp.Remote.Contracts/Requests.cs`, `src/MyTaskApp.Remote.Contracts/Catalog.cs`, `src/MyTaskApp.Application/QuickCommands/QuickCommandHandlers.cs` |

**Estado:** proposto (fase 2), em implementação: o contrato do catálogo (T-01) e o "pedir confirmação" cobrado no caso de uso (T-18) já existem; o `RemoteAllowed` e as operações ainda não.

**Contexto:** o app não tem allowlist de executáveis: o comando cadastrado
roda no `cmd`/`sh` como foi escrito (ADR-028). A confiança vem de quem
cadastrou, nesta máquina, ou de quem revisou (`ICommandReviewGate`).
`RequiresConfirmation` só é cobrado pela tela. `CommandVariables.UnsafeCharacter`
filtra só os valores de contexto.

**Decisão:**

1. **Tipos fechados:** `environment.inspect`, `worktree.provision`,
   `worktree.remove`, `command.run`, `agent.start`, `agent.prompt`,
   `agent.stop`, `database.copy`. Cada um tem um contrato tipado e um
   tradutor para os casos de uso existentes. Nenhum campo é texto de shell,
   SQL ou argumento de linha de comando.
2. **Comando só por id**, e só com `RemoteAllowed`. É uma coluna **só local**
   (fora do mapa do sync, então não muda de longe) que **cai quando o texto do
   comando muda**, aqui ou por sync. O `ICommandReviewGate` continua valendo.
3. **`RequiresConfirmation` é do caso de uso** (`RunQuickCommand.Confirmed`,
   ADR-051): a tela marca o pedido depois do diálogo, e o agente só o marca
   depois de uma aprovação remota.
4. **Parâmetros vindos de fora** passam por `UnsafeCharacter` e por um teto de
   tamanho, como os valores de contexto.
5. **Diretórios** só como ids (diretório da etiqueta, ambiente); o caminho é
   resolvido no agente, sem `..`, sem absoluto e sem link para fora do worktree.
6. **Catálogo publicado pelo agente**: diretórios, comandos, conexões e perfis
   do Claude liberados, com ids e nomes, **sem** caminho, host, usuário, texto
   de comando ou senha. O controlador monta a tela a partir dele.

## Alternativas

- **Comando livre com allowlist de executáveis** (`git`, `dotnet`, `npm`). Uma
  allowlist de executável não segura argumentos (`git -c core.sshCommand=…`,
  `npm run` de qualquer script) e o shell não interpreta o que a lista entende.
- **Comando livre com aprovação na máquina executora.** Não há ninguém nela.
- **Liberar todos os comandos já cadastrados.** Um comando escrito para rodar
  diante do usuário (um `deploy`) não deveria virar remoto sem uma escolha.

## Consequências

- Quem quer um comando novo de longe precisa cadastrá-lo e liberá-lo na máquina
  executora. É o preço, e é de propósito.
- A tela de comandos ganha "Disponível para controle remoto".

## Fora de escopo, de propósito

- Criar ou editar comandos, conexões e diretórios de longe.
