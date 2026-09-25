# Gerar uma release

Do código ao instalador, localmente. Os detalhes de cada plataforma estão em
[`installer/README.md`](../installer/README.md); aqui está o processo.

## Pré-requisitos

| | |
|---|---|
| .NET SDK | **10.0.401** (pinado em `global.json`) |
| Inno Setup | **6** — `winget install -e --id JRSoftware.InnoSetup` (só para o instalador Windows) |
| `dotnet-ef` | `dotnet tool restore` (só para criar migrations) |

## 1. Subir a versão

Um arquivo, uma linha — `Directory.Build.props`:

```xml
<VersionPrefix>1.1.0</VersionPrefix>
```

É tudo. A partir daí a versão flui sozinha:

```
Directory.Build.props
   ├─→ assembly (FileVersion, InformationalVersion, Product, Company, Copyright)
   ├─→ instalador (AppVersion, VersionInfoVersion, nome do .exe)
   ├─→ "Aplicativos Instalados" do Windows
   ├─→ entrada .desktop do Linux
   └─→ log do app, na linha ApplicationStarted
```

Há teste (`VersioningAndBuildTests`) que quebra a build se alguém escrever um
número de versão em qualquer outro lugar.

`src/MyTaskApp.Desktop/app.manifest` segue em `1.0.0.0` de propósito: aquilo é
identidade de assembly do Win32, não a versão exibida, e mexer nela não muda
nada para o usuário.

## 2. Conferir

```powershell
dotnet build MyTaskApp.slnx -c Release    # TreatWarningsAsErrors=true
dotnet test  MyTaskApp.slnx               # 5 suítes
.\scripts\coverage.ps1 -Open              # mesmo portão de cobertura do CI
```

O CI reprova com cobertura abaixo do mínimo (linhas e branches, definidos em
`scripts/coverage.ps1`) e comenta no PR o alcançado contra o mínimo. O
relatório HTML linha a linha fica em `artifacts/coverage/report`.

## 3. Empacotar

```powershell
# Windows → artifacts/installer/MyTaskAppSetup-<versão>.exe
.\installer\windows\build.ps1
```

```bash
# Linux → artifacts/installer/MyTaskApp-<versão>-linux-x64.tar.gz
installer/linux/build.sh
```

Cada script roda os testes, publica self-contained e empacota. `-SkipTests`
existe para iterar no instalador, não para gerar release.

## 4. Verificar antes de publicar

Rode o **teste de fumaça manual** de `installer/README.md` — é o que nenhum
teste automatizado alcança, em especial: instalar a versão nova sobre a antiga
e conferir que as tarefas continuam lá.

## 5. Publicar

```bash
git tag v1.1.0
git push origin v1.1.0
```

A tag dispara `.github/workflows/release.yml`, que gera os dois artefatos e
abre um rascunho de release no GitHub. O rascunho é de propósito: alguém olha
antes de o mundo baixar.

## Por que self-contained

O instalador tem ~53 MB porque leva o runtime .NET 10 dentro. A alternativa
(~8 MB) exigiria que o usuário instalasse o .NET Desktop Runtime antes — mais
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
