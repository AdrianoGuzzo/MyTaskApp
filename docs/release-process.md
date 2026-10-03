# Processo de release

Do commit ao instalador, sem ninguém editar número de versão. A decisão e os
motivos estão no ADR-044 (`docs/ARCHITECTURE.md`). Os detalhes de cada
instalador estão em [`installer/README.md`](../installer/README.md).

```
commit convencional → PR (título checado) → CI → squash no master
   → Release Please abre/atualiza o PR "chore(master): release X.Y.Z"
   → merge desse PR → tag vX.Y.Z + release em rascunho
   → release.yml: confere tag × código → testes → instaladores → SHA256SUMS
   → anexa ao rascunho → alguém roda o teste de fumaça e publica
```

## Para quem desenvolve: o dia a dia

1. Branch a partir do master: `feature/…`, `fix/…`.
2. Commits do jeito que quiser. O que conta é o **título do PR**, porque o
   merge é squash e o título vira o único commit no master.
3. Título do PR no formato `tipo: descrição` (veja a tabela abaixo). O
   workflow `PR title` reprova um título fora do padrão.
4. CI verde, merge. Pronto: a versão, o changelog e a release são do Release
   Please.

Ninguém edita `VersionPrefix`, `CHANGELOG.md`, o `.iss` ou o manifest à mão
para subir versão.

### Adicionar uma funcionalidade, passo a passo

```bash
git switch master && git pull
git switch -c feature/exportar-csv
# … código, testes, commits livres …
git push -u origin feature/exportar-csv
gh pr create --title "feat: exporta as tarefas do dia em CSV"
```

Depois do merge, o PR de release passa a listar "exporta as tarefas do dia em
CSV" em **Added**, e a próxima versão proposta sobe o MINOR.

## 1. Conventional Commits

`tipo(escopo opcional): descrição`. A descrição é em português e é o que vai
para o changelog, então escreva para quem usa o app.

| Tipo | Quando | Seção no changelog | Versão |
|---|---|---|---|
| `feat` | funcionalidade nova | Added | MINOR |
| `fix` | correção de bug | Fixed | PATCH |
| `perf` | ficou mais rápido, sem mudar o comportamento | Performance | PATCH |
| `refactor` | reorganização sem mudar o comportamento | Changed | PATCH |
| `revert` | desfaz um commit anterior | Reverted | PATCH |
| `docs`, `test`, `build`, `ci`, `chore`, `style` | o resto | — (oculto) | nenhuma |

Exemplos:

```text
feat: adiciona sincronização de tarefas
fix: corrige erro na importação do SQLite
perf: otimiza a consulta do quadro de hoje
refactor(agents): reorganiza o monitor de sessões
docs: atualiza o processo de release
chore: atualiza dependências
```

**Breaking change** sobe o MAJOR. Use `!` depois do tipo, ou um rodapé
`BREAKING CHANGE:` no corpo do PR (que vira o corpo do commit no squash):

```text
feat!: remove o formato antigo de backup

BREAKING CHANGE: backups gerados antes da 2.0 não são mais importados.
```

Só os PRs mesclados no master contam. Os commits de dentro do PR somem no
squash.

## 2. Semantic Versioning e quando gerar versão

`MAJOR.MINOR.PATCH`, tag `vMAJOR.MINOR.PATCH`.

| | Use para | Exemplo |
|---|---|---|
| **PATCH** | bug fix, correção de segurança, pequenas correções técnicas, nada que quebre contrato | 1.4.0 → 1.4.1 |
| **MINOR** | funcionalidade nova, opção nova, melhoria compatível | 1.4.0 → 1.5.0 |
| **MAJOR** | breaking change: remoção de recurso, dado ou formato que exige migração incompatível | 1.5.0 → 2.0.0 |

Uma versão é uma **entrega**, não um commit. O PR de release vai acumulando os
merges até alguém decidir que é hora. Ele só é mesclado quando há algo a
entregar.

Migration de banco **não** é breaking change por si: o app aplica as pendentes
no start (seção Migrations, abaixo). Breaking é quando uma versão nova não
consegue ler os dados que a anterior deixou.

### Regras que não se negociam

- **Nunca reaproveitar uma versão.** Se a 1.5.0 saiu errada, a correção é a 1.5.1.
- **Nunca mover uma tag.** A tag aponta para o commit que gerou os instaladores,
  para sempre.
- **Release publicada é imutável.** O pipeline recusa refazer uma release
  publicada e nunca substitui um artefato (`gh release upload` sem `--clobber`).
- Cliente A na 1.4.0, cliente B na 1.4.1, cliente C na 1.5.0: cada um tem uma
  tag, um commit e um instalador com hash publicado.

## 3. Como criar uma release

1. Abra o PR **`chore(master): release X.Y.Z`** (o Release Please o mantém
   aberto e atualizado a cada merge no master).
2. Confira a versão proposta e o `CHANGELOG.md` do diff. Para mudar o texto de
   uma entrada, edite o título do PR original no GitHub e o Release Please
   regenera o PR de release.
3. Mescle (squash). Em poucos minutos:
   - a tag `vX.Y.Z` existe, no commit do merge;
   - a release `vX.Y.Z` existe em **rascunho**, com as notas do changelog;
   - o workflow `Release Please` → job `build` gera os instaladores e os anexa.
4. Baixe o instalador do rascunho e rode o **teste de fumaça manual** de
   `installer/README.md`. É o que nenhum teste automatizado alcança. O mais
   importante: instalar por cima da versão anterior e conferir que as tarefas
   continuam lá.
5. Publique o rascunho em *Releases*.

Para forçar um número específico (raro), ponha no corpo do PR que vai ser
mesclado o rodapé `Release-As: 2.0.0`.

### Se o build do rascunho falhar

Nada foi publicado. Corrija a causa e escolha:

- **Falha de infraestrutura** (runner, rede, Inno): *Actions → Release → Run
  workflow*, informando a tag. O workflow só aceita rascunho sem artefatos; se
  ficou algum pela metade, apague-o no rascunho antes.
- **Falha de código** (teste vermelho na tag): apague o rascunho e corrija com
  um `fix:` no master, que gera a próxima versão. A tag com defeito fica no
  histórico, sem release.

## 4. CI (`.github/workflows/ci.yml`)

Em todo PR e em todo push no master e nos `release/v*`:

```
restore → build Release (TreatWarningsAsErrors) → testes das 5 suítes
       → portão de cobertura (scripts/coverage.ps1) → publish-smoke win-x64 e linux-x64
```

Qualquer passo vermelho reprova o PR. O `PR title` (`.github/workflows/pr-title.yml`)
roda junto e reprova título fora do padrão.

Não há analyzer extra. O lint é o próprio build: `TreatWarningsAsErrors` +
`EnforceCodeStyleInBuild`.

## 5. Pipeline de release

**`.github/workflows/release-please.yml`**, a cada push no master ou num
`release/v*`:

- mantém o PR de release (versão no `.release-please-manifest.json` e no
  `Directory.Build.props`, entrada nova no `CHANGELOG.md`);
- quando o PR de release é mesclado: cria a tag e a release em rascunho
  (`draft` + `force-tag-creation` em `release-please-config.json`) e chama o
  `release.yml` com a tag.

**`.github/workflows/release.yml`** (só com uma tag que já existe):

| Job | Faz |
|---|---|
| `verify` | tag no formato `vX.Y.Z`; `dotnet msbuild -getProperty:Version` igual à tag; a release existe, é rascunho e não tem artefatos |
| `windows` | checkout **da tag** → `installer/windows/build.ps1` (testes → publish → Inno Setup) |
| `linux` | checkout **da tag** → `installer/linux/build.sh` |
| `publish` | confere os nomes → `SHA256SUMS` → `sha256sum -c` → anexa ao rascunho → acrescenta às notas o commit SHA, o link da execução e os hashes |

Artefatos de cada release:

```text
MyTaskApp-1.5.0-win-x64-setup.exe
MyTaskApp-1.5.0-linux-x64.tar.gz
SHA256SUMS
```

### Configuração do repositório (uma vez)

- *Settings → General → Pull Requests*: só **squash**, com mensagem padrão
  "Pull request title" (ou "title and description").
- *Settings → Actions → General → Workflow permissions*: marcar **Allow GitHub
  Actions to create and approve pull requests** (o Release Please abre PR).
- Opcional: secret **`RELEASE_PLEASE_TOKEN`**, um PAT fine-grained com
  *Contents* e *Pull requests* de escrita neste repositório. Sem ele, o PR de
  release nasce sem CI, porque eventos do `GITHUB_TOKEN` não disparam
  workflows. Isso não deixa passar nada: o `release.yml` refaz build e testes
  na tag antes de anexar qualquer artefato.

## 6. Gerar o instalador localmente

Pré-requisitos:

| | |
|---|---|
| .NET SDK | **10.0.401** (pinado em `global.json`) |
| Inno Setup | **6**: `winget install -e --id JRSoftware.InnoSetup` (só para o instalador Windows) |
| `dotnet-ef` | `dotnet tool restore` (só para criar migrations) |

```powershell
# Windows → artifacts/installer/MyTaskApp-<versão>-win-x64-setup.exe
.\installer\windows\build.ps1
```

```bash
# Linux → artifacts/installer/MyTaskApp-<versão>-linux-x64.tar.gz
installer/linux/build.sh
```

São os mesmos scripts que a pipeline roda. A versão é a do
`Directory.Build.props` no commit em que você está. Num commit entre releases
ela é a última publicada, e o SHA no exe diz que o código é outro.
`-SkipTests` existe para iterar no instalador, não para gerar release.

Para conferir antes de abrir PR:

```powershell
dotnet build MyTaskApp.slnx -c Release    # TreatWarningsAsErrors=true
.\scripts\coverage.ps1 -Open              # mesmo portão de cobertura do CI
Get-FileHash artifacts\installer\*.exe -Algorithm SHA256
```

Para ver o PR de release que o Release Please abriria, sem criar nada (exige o
branch no GitHub e Node):

```powershell
npx release-please release-pr --dry-run --token (gh auth token) `
  --repo-url AdrianoGuzzo/MyTaskApp --target-branch <branch> `
  --config-file release-please-config.json --manifest-file .release-please-manifest.json
```

## 7. Hotfix

**Caso comum: o master não tem nada pendente além do conserto.** É o fluxo de
sempre: PR `fix: …` → merge → o PR de release propõe `1.5.1` → merge.

**O master já tem features que não podem sair junto.** A 1.5.1 precisa conter
só a correção, então ela nasce de um branch de manutenção a partir da tag:

```bash
git fetch --tags
git switch -c release/v1.5.x v1.5.0
git push -u origin release/v1.5.x          # o Release Please passa a cuidar deste branch

git switch -c hotfix/corrige-lembrete release/v1.5.x
git cherry-pick <sha-do-fix>              # ou escreva o conserto aqui
git push -u origin hotfix/corrige-lembrete
gh pr create --base release/v1.5.x --title "fix: corrige o lembrete que não tocava"
```

Depois do merge, o Release Please abre o PR `chore(release/v1.5.x): release
1.5.1` **contra o branch de manutenção**. Mesclar cria a tag `v1.5.1` e o
rascunho com os instaladores, como numa release normal.

Por fim, leve o mesmo conserto para o master com um PR `fix: …` comum, senão a
1.6.0 sai sem ele. O branch `release/v1.5.x` pode ficar para uma 1.5.2.

O app do MyTaskApp ajuda: o combo **Tag (opcional)** do "Iniciar
implementação" (ADR-043) cria o worktree direto em `v1.5.0`.

## 8. Verificar uma versão instalada

- **No app:** menu ☰ do painel → última linha, `MyTaskApp 1.5.0 · a82f91c ·
  2026-10-02` (versão, commit, data da build). Clicar copia os três, com o SHA
  inteiro, para colar num relato de problema.
- **No log:** `%APPDATA%\MyTaskApp\logs`, linha `ApplicationStarted` com
  versão, commit e data.
- **No Windows:** *Configurações → Aplicativos Instalados* mostra a versão. As
  *Propriedades* do `MyTaskApp.exe` → *Detalhes* → *Versão do produto* mostra
  `1.5.0+<sha>`. Pelo terminal:

  ```powershell
  (Get-Item "$env:LOCALAPPDATA\Programs\MyTaskApp\MyTaskApp.exe").VersionInfo.ProductVersion
  reg query HKCU\Software\MyTaskApp /v Version
  ```

- **No Linux:** `X-AppVersion` em `~/.local/share/applications/mytaskapp.desktop`.
- **O instalador é o publicado?** Baixe o `SHA256SUMS` da release:

  ```bash
  sha256sum -c SHA256SUMS --ignore-missing
  ```

  ```powershell
  (Get-FileHash MyTaskApp-1.5.0-win-x64-setup.exe -Algorithm SHA256).Hash.ToLower()
  # compare com a linha do SHA256SUMS ou das notas da release
  ```

## 9. Identificar o commit de uma versão

```bash
git rev-list -n 1 v1.5.0                  # commit da tag
git show v1.5.0 --stat                    # o que entrou
git log v1.4.0..v1.5.0 --oneline          # tudo entre duas versões
git switch -c investiga-1.5.0 v1.5.0      # o código exato que o cliente tem
```

As notas da release também trazem o SHA e o link do commit, na seção
*Rastreabilidade*. E o SHA que o app mostra no menu é o mesmo que o GitHub
mostra ao lado do commit.

## 10. Assinatura digital (preparado, não ligado)

Hoje os instaladores saem **sem assinatura**, e o SmartScreen avisa na
primeira execução. O gancho existe: a linha `SignTool=` comentada no
`MyTaskApp.iss` e o `build.ps1 -Sign`. Para ligar quando houver certificado:

1. Guardar o certificado **só** em secrets do GitHub, por exemplo
   `WINDOWS_SIGNING_CERT` (PFX em base64) e `WINDOWS_SIGNING_PASSWORD`. Nunca
   no repositório, nem cifrado.
2. No job `windows` do `release.yml`, antes do build: decodificar o PFX para
   `$env:RUNNER_TEMP`, configurar o sign tool `mytaskapp` do Inno com o
   `signtool sign /fd sha256 /tr <timestamp> /td sha256 /f … /p … $f` e chamar
   `build.ps1 -Sign`.
3. Descomentar `SignTool=mytaskapp` e `SignedUninstaller=yes` no `.iss`.

Não está implementado porque não dá para verificar sem um certificado. Um
passo de assinatura que nunca rodou daria a impressão de existir.

Nenhum outro secret é necessário: o pipeline usa o `GITHUB_TOKEN` (e,
opcionalmente, o `RELEASE_PLEASE_TOKEN`).

## Por que self-contained

O instalador tem ~53 MB porque leva o runtime .NET 10 dentro. A alternativa
(~8 MB) exigiria que o usuário instalasse o .NET Desktop Runtime antes: mais
uma tela, mais um download, mais um pedido de elevação, e um modo de falha
("instalou e não abre") que é caro de diagnosticar à distância.

Trimming continua **desligado**: EF Core e Avalonia dependem de reflexão.
`InvariantGlobalization` continua **false**: sem ICU, `America/Sao_Paulo` não
resolve e o agendamento do ADR-002 quebra.

## Migrations

O instalador **nunca** toca no banco. Uma versão que traz migration nova só
precisa dela commitada:

```bash
dotnet tool restore
dotnet ef migrations add <Nome> \
  --project src/MyTaskApp.Infrastructure \
  --startup-project src/MyTaskApp.Infrastructure
```

A aplicação aplica o que estiver pendente no próximo start, antes da primeira
tela (`Program.PrepareDatabase` → `DatabaseInitializer` → `MigrateAsync`).
`UpgradePreservationTests` cobre esse momento contra SQLite de verdade.
