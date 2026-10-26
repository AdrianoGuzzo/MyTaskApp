# ADR-049 — Instalador fecha o app aberto, em vez de pedir que alguém o feche

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | distribuicao |
| **Registrado** | 2026-10-04, commit `a7ab82e` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | Inno Setup, PrepareToInstall, taskkill, WMI, CloseApplications, AppMutex, usAppMutexCheck, atualização silenciosa |
| **Relações** | revisa: [ADR-019](019-instancia-unica-por-mutex-nomeado.md); relacionado: [ADR-016](016-bandeja-e-fechar-para-esconder.md), [ADR-085](085-agente-sem-tela-sobre-o-mesmo-sqlite.md) |
| **Código** | `installer/windows/MyTaskApp.iss`, `tests/MyTaskApp.Packaging.Tests/WindowsInstallerContractTests.cs` |

**Contexto:** o `.iss` usava `AppMutex` (ADR-019). Com o app aberto, o Inno
mostrava "feche todas as instâncias e clique em OK" e ficava esperando. Só que o
app vive na bandeja (ADR-016): o usuário não sabe onde está a "instância", e
fechar a janela só a manda de volta para lá. O `CloseApplications=yes` também
não resolvia, porque o Restart Manager pede para a janela fechar, e fechar é
esconder.

**Decisão:** o instalador avisa e fecha.

- `PrepareToInstall` lista, por WMI, todo processo cujo **executável mora
  dentro de `{app}`**, mostra a lista num aviso ("será fechado para continuar a
  instalação", OK/Cancelar) e encerra cada um com `taskkill /F /PID`. Espera
  eles morrerem (até 10s) antes de deixar o Inno copiar arquivos. Cancelar para
  na página *Preparando para Instalar* sem mexer em nada.
- O **resto** é do Restart Manager: `CloseApplications=force` com
  `CloseApplicationsFilter=*.*` fecha qualquer outro processo que segure um
  arquivo da pasta (um terminal, um editor). Ele roda depois do
  `PrepareToInstall`, então já não encontra o MyTaskApp.
- O desinstalador faz o mesmo em `usAppMutexCheck`: depois do "tem certeza?" e
  antes de apagar qualquer arquivo.
- Numa atualização **silenciosa** o aviso vale como OK, e o app que foi fechado
  volta no fim com `--startup`, na bandeja e sem elevação
  (`runasoriginaluser`). Com assistente, quem decide é a caixa "Iniciar o
  MyTaskApp", que já existia.

**Pelo caminho, não pelo nome.** Um MyTaskApp rodando de outro lugar (uma build
de desenvolvimento) não segura nenhum arquivo da instalação e não tem por que
morrer. O `unins000.exe` fica de fora da lista: ele mora em `{app}` e é quem
está desinstalando. O mutex segue como contrato com o `SingleInstance`, mas
virou detecção de reserva: só decide quando o WMI não responde, e aí o
encerramento é por nome (`/IM MyTaskApp.exe`).

**Forçado, e sem `/T`.** Não há como pedir para o app sair: fechar a janela o
manda para a bandeja, e a versão que está sendo substituída é a antiga, que não
conheceria um pedido novo de "encerre-se". Matar é seguro para os dados porque o
banco é SQLite (uma transação interrompida é desfeita no próximo start). Sem
`/T` porque os filhos do app (agentes, terminais, `gh`) são trabalho do usuário
e não rodam de dentro de `{app}`.

**Limites aceitos:**

- o ícone da bandeja do processo morto fica até o mouse passar por cima, como
  em qualquer processo encerrado à força;
- o tamanho e a posição da janela do último instante não são gravados — o
  `widget.json` fica com o que foi salvo no último movimento;
- listar processos por WMI leva uns dois segundos, que somam ao *Preparando
  para Instalar* de toda atualização. Numa instalação nova a pasta não existe e
  a listagem nem acontece.

**Testes:** `WindowsInstallerContractTests` trava as decisões (sem `AppMutex=`,
aviso com OK por padrão, `force` + `*.*`, `usAppMutexCheck`, nada de `/T`,
reabertura silenciosa na bandeja). O comportamento foi conferido num Windows
real com uma cópia isolada do instalador (outro `AppId`, outro nome, outra
pasta): atualização silenciosa com o "app" aberto e um segundo processo
travando `appsettings.json` — o primeiro saiu pelo `taskkill`, o segundo pelo
Restart Manager, e a instalação terminou com código 0; desinstalação silenciosa
com o app aberto removeu a pasta inteira.
