# ADR-023 — Iniciar com o Windows, e a chave `Run` como única verdade

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | distribuicao |
| **Registrado** | 2026-09-21, commit `e5aa2e8` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | --startup, HKCU Run, IStartupRegistration, WindowsStartupRegistration, startupicon, bandeja, início automático |
| **Relações** | revisa: [ADR-016](016-bandeja-e-fechar-para-esconder.md), [ADR-019](019-instancia-unica-por-mutex-nomeado.md); exceção a: [ADR-018](018-instalador-inno-setup-e-dados-do-usuario.md); exceção aberta por: [ADR-048](048-hud-visivel-sem-atrapalhar.md) |
| **Código** | `src/MyTaskApp.Desktop/Composition/WindowsStartupRegistration.cs`, `src/MyTaskApp.Desktop/Composition/IStartupRegistration.cs`, `src/MyTaskApp.Desktop/Composition/LaunchOptions.cs`, `installer/windows/MyTaskApp.iss` |

**Decisão:** o instalador oferece **"Iniciar o MyTaskApp com o Windows"**,
**marcada por padrão**, e grava
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\MyTaskApp` com
`"<exe>" --startup`. O app aprende esse argumento e, quando lançado por ele,
sobe **direto para a bandeja**. O menu ☰ do painel liga e desliga a mesma
entrada depois, sem reinstalar.

**Por que agora:** ADR-016 e ADR-019 listaram isto como fora de escopo, e o
ADR-019 pediu explicitamente que mudar de ideia viesse como decisão própria —
é este ADR. O que força a mão é a premissa do próprio ADR-016: *"o sistema fica
responsável por me lembrar" só é verdade se o processo estiver vivo às 15:00*.
Sem início automático, todo reinício da máquina desarma o produto inteiro, e
justamente para o usuário que mais precisa dele — o que já esqueceu da tarefa
não vai lembrar de abrir o app que lembra por ele.

**A regra que organiza tudo:**

```
Iniciar com o Windows  →  a chave Run, e mais nada
```

Nem `widget.json`, nem o banco, nem um marcador próprio do instalador. O mesmo
raciocínio do `UserDataLocation` no ADR-018: três cópias da mesma regra é como
uma delas acaba diferente. Aqui a segunda cópia teria um sintoma concreto — o
usuário desliga pelo menu, atualiza o app, e o início automático volta sozinho.

**Por que `HKCU`, e nunca `HKA`:** o resto do `[Registry]` usa `HKA`, que numa
instalação `/ALLUSERS` vira `HKLM`. Ali seria fatal: o app roda **sem elevação**
e conseguiria ler a entrada mas nunca apagá-la — a opção do menu viraria um
interruptor que só liga. Quem liga o início automático é uma pessoa, não uma
máquina.

**Por que `--startup` e não a preferência que já existia.** "Abrir recolhido da
próxima vez" (`WidgetState.StartHidden`) responde outra pergunta: como o app
abre quando **o usuário** o abre. O login é diferente — ninguém clicou em nada,
e um painel pulando na frente de quem acabou de ligar a máquina é exatamente o
oposto do que a opção promete. As duas origens convivem em `StartHiddenIfAsked`
sem uma mandar na outra. Pelo mesmo motivo, um segundo lançamento com
`--startup` **não** revela a janela do primeiro, ao contrário do clique no
atalho do ADR-019.

**Por que marcada, se o atalho da área de trabalho é desmarcado.** Não é
incoerência: o atalho é conveniência que o usuário não pediu — ruído. Iniciar
com o Windows é a entrega da promessa central do produto. Quem não quer desmarca
numa caixa visível, e em `/VERYSILENT` a opção vem ligada (`/MERGETASKS`
`"!startupicon"` desliga).

**A atualização pergunta ao registro.** `UsePreviousTasks` (ligado por padrão)
restauraria a escolha gravada pelo instalador anterior, desfazendo **em
silêncio** um "desliga isso" feito pelo menu do app. Então `InitializeWizard`
pré-marca a caixa pelo estado real da chave numa atualização, e deixa o padrão
marcado valer só na instalação nova. O item é localizado pelo texto da
descrição, mas os dois lados saem do mesmo `{cm:StartupTask}` — não há como
divergirem.

**A desinstalação apaga o valor sempre**, e não só via `uninsdeletevalue`: se a
entrada foi criada pelo menu do app, não existe registro de desinstalação para
o Inno reverter, e sobraria no `Run` um caminho para um `.exe` que não existe
mais — falhando calado a cada login. É registro de aplicação, não dado do
usuário, então sai sem pergunta; o `[UninstallDelete]` continua vazio e o único
`DelTree` continua sendo o de `RemoveUserData`.

**Interface, contra o que o ADR-018 prescreve.** Lá a regra é "classe comum, sem
interface, porque não há segunda implementação". Aqui há: o Windows e o objeto
nulo que responde `IsSupported = false` no Linux e nos testes headless —
escrever no registro de verdade durante `dotnet test` não é opção. As guardas
de plataforma ficam nos métodos públicos e os privados são
`[SupportedOSPlatform("windows")]`; sem isso o CA1416 quebra a build, que tem
`TreatWarningsAsErrors`. O helper de escrita não usa lambda de propósito: o
analisador examina o corpo de uma lambda como se ele pudesse rodar em qualquer
plataforma, e a guarda do método que a criou não vale lá dentro.

**O menu usa comando, não `Mode=TwoWay`** — ao contrário do "abrir recolhido"
que está uma linha acima dele. A escrita no registro pode falhar (política de
grupo, perfil em rede), e um visto marcado sobre uma escrita que não aconteceu
prometeria um app que não vai subir no próximo login. O comando relê o estado e
mostra a verdade; o porquê fica no log.

**Limites aceitos:**

- o Gerenciador de Tarefas > *Inicializar* pode **desabilitar** a entrada sem
  apagar o valor (`StartupApproved`). Nesse caso o visto do app discorda do
  Windows, e quem manda é o Windows. Ler `StartupApproved` seria passar a
  depender de um detalhe não documentado para responder uma pergunta que o
  próprio sistema já responde melhor;
- `/ALLUSERS` registra o início automático só para quem instalou — os demais
  ligam pelo menu do app;
- o toggle grava `Environment.ProcessPath`. Ligado a partir de um build de
  desenvolvimento, ele aponta o `Run` para o binário de `bin`/`artifacts`, e o
  Windows passa a subir esse. `MYTASKAPP_DATA_DIR` isola banco e logs, mas não
  isola o registro;
- o menu da bandeja ficou de fora: já tem nove itens, e "Abrir" está a um clique
  do menu do painel.

**Fora de escopo, e de propósito: o equivalente no Linux.** Seria um `.desktop`
em `~/.config/autostart`, espelhando `mytaskapp.desktop.in`. O pedido era
Windows, e o Linux não tem a bandeja como centro da experiência que torna isto
necessário lá.

## Divergências com o código (conferido em 2026-10-24)

- O ADR diz que InitializeWizard pré-marca a caixa pelo estado real da chave; o instalador faz isso em CurPageChanged (wpSelectTasks), com comentário explicando por que não em InitializeWizard (installer/windows/MyTaskApp.iss).
