# ADR-044 — Versionamento: o Release Please sobe a versão, a tag não se move

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | distribuicao |
| **Registrado** | 2026-10-02, commit `2b073ae` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | Release Please, Conventional Commits, CHANGELOG, VersionPrefix, SHA256SUMS, AppVersion, release.yml, pr-title.yml |
| **Relações** | revisa: [ADR-018](018-instalador-inno-setup-e-dados-do-usuario.md); relacionado: [ADR-016](016-bandeja-e-fechar-para-esconder.md), [ADR-043](043-tag-opcional-no-worktree.md) |
| **Código** | `.github/workflows/release-please.yml`, `.github/workflows/release.yml`, `release-please-config.json`, `src/MyTaskApp.Desktop/Composition/AppVersion.cs` |

**Contexto:** a versão já tinha fonte única (ADR-018), mas alguém precisava
lembrar de editar o `VersionPrefix`, criar a tag na mão e escrever as notas.
Não havia changelog, checksum nem um jeito de dizer, de dentro do app, que
código estava instalado. Com clientes em versões diferentes, "qual versão você
tem?" precisa levar a um commit.

**Decisão:** **Release Please** (`googleapis/release-please-action@v4`,
`release-type: simple`, manifest). Ele lê os Conventional Commits desde a
última release e mantém aberto um PR `chore(master): release X.Y.Z` com o
`CHANGELOG.md` e o número novo. Mesclar o PR cria a tag `vX.Y.Z` e a release em
rascunho, e o mesmo workflow chama o `release.yml`, que gera os instaladores
**a partir da tag**. Processo de uso em `docs/release-process.md`.

- **Por que o Release Please, e não MinVer ou git-cliff.** O que importa é o
  commit da tag já conter o próprio changelog e a própria versão. Com git-cliff
  o changelog viraria um commit depois da tag. Com MinVer a versão sairia da
  tag no build, mas o Release Please já guarda o número no manifest, e seriam
  duas fontes. Nenhum pacote NuGet nem Node entra no repositório: é uma action.
- **A versão continua em `VersionPrefix`**, agora editada pelo bot (anotação
  `x-release-please-version` na linha). Os scripts de empacotamento não
  mudaram: continuam lendo `-getProperty:Version`. O manifest repete o número
  por exigência da ferramenta, e `ReleaseProcessTests` quebra a build se os
  dois divergirem.
- **Squash merge, título do PR no padrão.** O título vira o único commit no
  master, então só ele precisa estar no padrão, e o `pr-title.yml` reprova o
  que não estiver. Exigir isso de todo commit de todo PR seria atrito diário
  para nada.
- **Rascunho com a tag já criada** (`draft` + `force-tag-creation`). O
  rascunho preserva o teste de fumaça manual antes de alguém baixar. Sem
  `force-tag-creation`, o GitHub só cria a tag ao publicar, e a release
  seguinte não acharia a anterior. Isso dá um changelog com o histórico
  inteiro.
- **`release.yml` não cria nada, só anexa.** Ele confere a tag no formato
  `vX.Y.Z` e se `Version` é igual à tag; exige rascunho **sem artefatos**;
  sobe com `gh release upload` sem `--clobber`. Release publicada não se
  refaz: um cliente pode já ter aquele arquivo. Sai o gatilho de push de tag.
  Tag criada com `GITHUB_TOKEN` não dispararia nada, e com um PAT dispararia em
  dobro. Quem chama o build é o `release-please.yml`.
- **Nomes e hashes.** `MyTaskApp-{versão}-{rid}-setup.exe` e
  `MyTaskApp-{versão}-{rid}.tar.gz` (o Windows deixou de ser
  `MyTaskAppSetup-{versão}.exe`), mais um `SHA256SUMS`. As notas ganham o SHA
  do commit e os hashes.
- **Hotfix sem as features do master:** branch de manutenção `release/vX.Y.x`
  a partir da tag. O Release Please roda nele também (`target-branch` é o
  branch do push) e propõe o PATCH só com o que entrou ali. O ADR-043 cria o
  worktree direto na tag.

**Versões do assembly.** `AssemblyVersion` e `FileVersion` ficam como o SDK
deriva (`M.m.p.0`): o exe não tem consumidor externo nem strong name, então
não há binding para quebrar. `InformationalVersion` é o que se mostra: o SDK já
cola o SHA (`1.5.0+<sha>`), via Source Link. O csproj do Desktop acrescenta um
`AssemblyMetadata("BuildDate")`.

**Onde a versão aparece.** `Composition/AppVersion` separa versão, commit e
data. A última linha do menu ☰ mostra `MyTaskApp 1.5.0 · a82f91c ·
2026-10-02`, e o clique copia os três com o SHA inteiro. A linha
`ApplicationStarted` do log leva os mesmos três. Não há janela "Sobre": seria
uma tela inteira para uma linha. O balão da bandeja ficou como estava, porque
ele já é o contador de pendências (ADR-016).

**Primeira versão.** Nunca houve tag nem release. O manifest começa em
`1.0.0`, a versão das builds anteriores, com `bootstrap-sha` no master de
quando o processo entrou, para não varrer um histórico que não segue o padrão.
A primeira release da pipeline é a **1.1.0**.

**Limites aceitos:**

- sem `RELEASE_PLEASE_TOKEN`, o PR de release nasce sem CI (eventos do
  `GITHUB_TOKEN` não disparam workflows). O `release.yml` roda os testes na tag
  antes de anexar qualquer coisa, e o PR só mexe em versão e changelog;
- se o build do rascunho falhar por código, aquela tag fica sem release, e o
  conserto é a próxima versão;
- a data da build é a do dia em que se compilou, não a do commit. Duas builds
  locais do mesmo commit em dias diferentes mostram datas diferentes, e o SHA
  é o que identifica o código;
- assinatura digital continua só preparada (`docs/release-process.md`, seção
  10): sem certificado não dá para verificar, e um passo que nunca rodou daria
  a impressão de existir.

## Divergências com o código (conferido em 2026-10-24)

- O ADR cita googleapis/release-please-action@v4; o workflow usa a v5.0.0 fixada por SHA (.github/workflows/release-please.yml).
- O ADR lista dois artefatos (setup.exe e tar.gz); o release.yml também exige e publica MyTaskApp-{versão}-android.apk (.github/workflows/release.yml).
