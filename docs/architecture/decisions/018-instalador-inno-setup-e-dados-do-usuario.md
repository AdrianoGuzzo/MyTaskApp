# ADR-018 — Distribuição: Inno Setup, e uma linha divisória entre aplicação e dados

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | distribuicao |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | Inno Setup, instalador, UserDataLocation, MYTASKAPP_DATA_DIR, appsettings.user.json, self-contained, VersionPrefix, RemoveUserData |
| **Relações** | revisto por: [ADR-044](044-versionamento-com-release-please.md); exceção aberta por: [ADR-023](023-iniciar-com-windows-chave-run.md); relacionado: [ADR-002](002-hora-de-parede-no-dominio.md), [ADR-005](005-persistencia-por-interfaces-estreitas.md), [ADR-042](042-som-por-estado-do-claude-code.md), [ADR-078](078-servidor-de-sync-do-usuario.md) |
| **Código** | `installer/windows/MyTaskApp.iss`, `installer/windows/build.ps1`, `src/MyTaskApp.Infrastructure/Storage/UserDataLocation.cs`, `tests/MyTaskApp.Packaging.Tests/WindowsInstallerContractTests.cs` |

**Decisão:** o MyTaskApp é distribuído como instalador. No Windows, **Inno
Setup 6** gerando um `.exe` por usuário; no Linux, tarball com `install.sh`;
macOS fica documentado e não implementado. Publicação **self-contained**, sem
trimming. Tudo vive em `installer/`, fora de `src/`.

**Por que Inno Setup e não WiX/MSI:** o MSI paga um preço alto — XML verboso,
harvest de centenas de arquivos, UI datada — por benefícios que este app não
usa (GPO, rollback transacional do Windows Installer, patching). O Inno entrega
`.exe` pequeno, instalação por usuário **sem UAC**, `/VERYSILENT`, detecção de
upgrade por `AppId` e log próprio, com um arquivo de script legível. É o que
VS Code, Docker Desktop e Git for Windows usam.

**Por que não Velopack/Squirrel:** trazem updater embutido e acoplam o
`Program.Main` ao framework de atualização. O briefing pede explicitamente para
não construir updater complexo antes de ter base sólida de instalação.

**A regra que organiza tudo:**

```
Aplicação → diretório de instalação   (substituído a cada atualização)
Dados     → pasta de dados do usuário (nunca tocada)
```

Antes deste ADR, `%APPDATA%/MyTaskApp` era calculado **três vezes** — em
`LoggingSetup`, `WidgetStateStore` e `DatabaseOptions`. Três cópias da mesma
regra é como uma delas acaba diferente. `UserDataLocation`
(`Infrastructure/Storage/`) passou a ser a única a saber, e `MYTASKAPP_DATA_DIR`
move as três juntas.

**Classe comum, sem interface.** Não há segunda implementação nem nada a
substituir em teste além da raiz, que já entra por parâmetro (ADR-005). O
método de criação chama-se `CreateDirectories()`, e não `EnsureCreated()`, para
que o nome proibido pelo §19 não apareça nem por coincidência num grep.

**O instalador não conhece o banco.** Não o copia, não o lê, não o apaga e não
roda migration nenhuma. A aplicação continua dona da evolução do schema
(`Program.PrepareDatabase` → `MigrateAsync`). Há teste de packaging que falha
se a string `.db` ou `sqlite` aparecer no `.iss`.

**Desinstalar preserva.** `[UninstallDelete]` está **vazio**, e há exatamente um
`DelTree` no arquivo inteiro, dentro de `RemoveUserData`, alcançável só depois
de uma confirmação cujo botão padrão é **Não**. Em modo silencioso nada é
apagado sem `/DELETEDATA=1` explícito. Três testes cercam isso, porque é a
única falha desta entrega que o usuário não teria como desfazer.

**Configuração do usuário sobrevive ao upgrade.** `appsettings.json` mora no
diretório de instalação e é sobrescrito a cada atualização — então
`BuildConfiguration` ganhou uma segunda camada opcional,
`<dados>/appsettings.user.json`, que o instalador nunca toca. Era o caminho
por onde um `TimeZoneId` ajustado à mão se perdia.

**Logs separados por subárvore, não por nome:** instalador em
`%LOCALAPPDATA%\MyTaskApp\installer\logs`, aplicação em `%APPDATA%\MyTaskApp\logs`.
Quem procura "por que não instalou" não pode esbarrar em "por que o lembrete não
tocou". O Inno grava log **sempre** (`SetupLogging=yes`), não só com `/LOG` —
ninguém liga o log antes de o problema acontecer.

**Logger de bootstrap.** `appsettings.json` é carregado com `optional: false` e
ficava **fora** do `try` do `Main`: uma instalação sem esse arquivo matava o app
antes de existir log, o sintoma mais difícil de diagnosticar à distância. Agora
um logger de arquivo sobe primeiro e a leitura da configuração acontece dentro
do `try`. O `Log.CloseAndFlush()` antes de trocar o logger não é zelo: o sink de
arquivo abre o log do dia com trava exclusiva, e sem soltar primeiro o logger
definitivo não conseguiria abrir o mesmo caminho.

**Self-contained, ~53 MB.** A alternativa de ~8 MB exigiria o .NET Desktop
Runtime na máquina — mais uma tela, mais um download, mais um pedido de
elevação e um modo de falha ("instalou e não abre") caro de diagnosticar.
Trimming fica desligado (EF Core e Avalonia dependem de reflexão) e
`InvariantGlobalization` continua `false`, senão `America/Sao_Paulo` deixa de
resolver e o ADR-002 quebra.

**Versão com fonte única.** `VersionPrefix` em `Directory.Build.props` alimenta
o assembly, o instalador, a entrada em "Aplicativos Instalados" e o `.desktop`
do Linux. Os scripts de packaging leem com `dotnet msbuild -getProperty:Version`
em vez de repetir o número, e um teste quebra a build se alguém escrever versão
em outro lugar. Quem sobe o número é o Release Please, não uma pessoa (ADR-044).

**O assembly passou a se chamar `MyTaskApp`**, não `MyTaskApp.Desktop` — é o
nome do `.exe` que o usuário vê instalado. Custou atualizar três URIs
`avares://`, que usam o nome do assembly; os testes headless guardam isso,
porque um `avares://` errado só falharia ao abrir o app.

**Limite aceito:** o instalador não é assinado. O gancho está pronto (`SignTool`
no `.iss`, `-Sign` no `build.ps1`) e sem certificado o SmartScreen avisa na
primeira execução. Assinar é a correção; desligar o aviso não é.
