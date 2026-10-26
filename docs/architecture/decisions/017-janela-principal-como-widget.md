# ADR-017 — A janela principal é um widget, não uma aplicação

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ui-desktop |
| **Registrado** | 2026-09-20, commit `396e55f` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | MainWindow, widget, WidgetMode, widget.json, IWidgetStateStore, WidgetChromeViewModel, Tokens.axaml, ElementRole |
| **Relações** | revisto por: [ADR-041](041-temas-sete-paletas-e-automatico.md), [ADR-048](048-hud-visivel-sem-atrapalhar.md); relacionado: [ADR-022](022-ordem-manual-por-arrasto.md), [ADR-046](046-fonte-de-icones-embutida.md), [ADR-054](054-post-it-entidade-propria.md) |
| **Código** | `src/MyTaskApp.Desktop/Views/MainWindow.axaml`, `src/MyTaskApp.Desktop/ViewModels/WidgetChromeViewModel.cs`, `src/MyTaskApp.Desktop/Widget/IWidgetStateStore.cs`, `src/MyTaskApp.Desktop/Styles/Tokens.axaml` |

**Decisão:** a `MainWindow` deixou de ser uma janela de 900x700 com decoração do
sistema e virou um painel de 360x560 sem decoração, arrastável, com três modos
de exibição (`Expanded`, `Compact`, `Collapsed`) e memória de posição e tamanho.
O tema passou a ser **escuro por decisão** (o ADR-041 trouxe depois o claro e
"seguir o Windows"), com paleta própria em
`Styles/Tokens.axaml`.

**Por quê:** `RequestedThemeVariant="Default"` fazia o app seguir o modo escuro
do Windows, e `DockPanel Margin="40,32"` dentro de 900x700 dava o resto. O
resultado era uma chapa preta enorme na área de trabalho — o oposto de algo que
se deixa aberto enquanto se trabalha. A variante agora é fixa (`Dark`) e a cor
de fundo é carvão (`#17181C`), não preto: `#000` puro num painel pequeno e
sempre visível vira um buraco, e a borda arredondada some contra monitores
escuros.

**A paleta é reaproveitamento, não substituição.** `Tokens.axaml` reaponta as
chaves do Fluent que as telas já pediam (`SystemFillColorCautionBrush`,
`ControlStrokeColorDefaultBrush`, `AccentFillColorDefaultBrush`, …). Vestir
botão, campo e checkbox assim custa um dicionário; reescrever `ControlTheme`
custaria centenas de linhas e um template para manter a cada atualização do
Avalonia.

**Arrastar e redimensionar são declarativos.** No Avalonia 12,
`ExtendClientAreaChromeHints` não existe mais: o equivalente é
`WindowDecorations="None"` + `ExtendClientAreaToDecorationsHint="True"` e marcar
elementos com `WindowDecorationProperties.ElementRole` (`TitleBar`, `ResizeSE`,
…). Quem move a janela continua sendo o sistema. Os manipuladores de
`PointerPressed` com `BeginMoveDrag`/`BeginResizeDrag` ficaram como rede de
segurança para o caso de a plataforma ignorar o papel — se ela honrar, o
ponteiro nem chega até eles.

**O pino não mexe em geometria.** "Sempre no topo" não entra em nenhum caminho
de posição ou tamanho: `ApplyMode()` e `ClampToScreen()` só correm quando `Mode`
muda. Com o pino ligado o painel continua sendo arrastado, redimensionado,
movido entre monitores e clicado como antes, e nada reaplica o `Topmost` ao
ganhar foco. O erro tentador é implementar o pino como "travar o painel onde
está" — dois gestos diferentes no mesmo botão, e silencioso: o botão acende
igual, e só quem tenta arrastar descobre. `WidgetPinTests` guarda isso pelo lado
estrutural: alternar o pino não pode mexer em `Position`, `Width`, `Height`,
`CanResize` ou `WindowState`, e o estado gravado é comparado campo a campo.

**O que o pino leva junto é o modo discreto.** Fixar, na prática, quer dizer
"deixa isso aí sem me atrapalhar", então `ToggleTopmost` liga `IsGhost` com ele —
um clique entrega os dois. Os dois continuam **separáveis**: "Modo discreto" no
menu e na bandeja devolve a moldura sem soltar o pino. Isto revisa o "somente" do
item 8 da especificação; o que o item pedia de fato — a janela continuar móvel —
segue valendo e testado.

**Fixado, a lista mostra só o que falta.** O pino também tira a seção
`CONCLUÍDAS` do quadro (`TodayViewModel.HideCompleted`). O motivo é o mesmo do
modo discreto: fixado, o painel fica num canto sobre as outras janelas, e ali
altura é o recurso escasso — uma tarefa riscada empurra para fora da vista uma
que ainda espera. Três limites, e cada um tem teste:

- **Esconder não é desfazer.** `TotalCount`, `CompletedCount`, `ProgressLabel` e
  o balão da bandeja continuam contando tudo; só a lista encolhe. Marcar e
  desmarcar segue funcionando pelo checkbox das linhas que restaram.
- **Não custa consulta.** O quadro carregado fica em `_board`, e trocar o modo
  remonta as seções a partir dele. Recarregar seria pior que lento: apagaria a
  mensagem de erro que estivesse à vista, pelo simples gesto de fixar o painel.
- **Dia terminado não vira painel em branco.** Sem as concluídas, um dia todo
  feito esvazia a lista — e no modo discreto não sobra nem cabeçalho para
  explicar o vazio. Daí `EmptyMessage`: "Tudo concluído. Aproveite." quando
  houve trabalho, "Nada para hoje. Aproveite." quando não houve.

Quem decide é a moldura, mas quem monta a lista é o quadro de hoje, então a
`MainWindow` carrega a decisão de um para o outro (`ShowPendingOnly`) — no
`IsTopmost` e também no `DataContextChanged`, porque o pino restaurado do disco
chega sem passar por clique nenhum.

**Modo discreto é a ausência da moldura, não um quarto modo.** `IsGhost` é
preferência (mora no `widget.json`), e não um valor de `WidgetMode`: ele se
combina com Completo e Compacto em vez de competir com eles. No modo discreto o
fundo do painel vira `Transparent`, a borda e a sombra somem e o cabeçalho sai de
cena. Três consequências tiveram de ser compensadas, e cada uma tem teste:

1. **Sem cabeçalho não há barra de arrasto.** O `Border.widgetShell` assume
   `ElementRole="TitleBar"`, e o fundo é `Transparent` — e não nulo — porque
   fundo nulo não recebe ponteiro e arrastar por área vazia deixaria de existir.
   O custo aceito: o retângulo do painel captura cliques que iriam para a área de
   trabalho atrás dele.
1b. **Sem cabeçalho também não há porta de volta.** Por isso o alfinete flutua
   ao lado do "+", e não só o "+": ele é o único caminho *na janela* para
   desfazer o modo. A bandeja continua existindo, mas é um caminho que ninguém
   encontra sozinho — e um widget sem moldura de onde não se sai é um widget
   preso na frente da tela.
2. **A lista está dentro da área de arrasto.** `Border.row`, o `CaptureCard` e o
   "+" saem dela com `ElementRole="User"`. Sem isso, marcar uma tarefa viraria um
   empurrão na janela — a mesma armadilha nº 4, agora do outro lado.
3. **Sem fundo, texto claro sobre janela clara some.** Cada linha ganha a própria
   pílula translúcida em vez de um scrim no painel inteiro: entre as linhas fica
   a área de trabalho limpa, que é o ponto do modo.

`IsGhostActive` é o que a tela usa, e não `IsGhost`: **recolhido o modo discreto
não vale**, porque ali o cabeçalho é a única coisa desenhada e escondê-lo apagaria
o widget da tela. A captura não ocupa espaço até o "+" pedir (`IsCaptureOpen`, de
sessão — não vai para o disco), e voltar para a moldura fecha a que ele abriu,
senão sobrariam duas capturas na tela.

**A moldura não é o `DataContext`.** O `DataContext` da janela continua sendo o
`TodayViewModel`; o estado do widget mora numa propriedade `Chrome` da própria
janela (`WidgetChromeViewModel`), ligada por `{Binding #Shell.Chrome.X}`. Trocar
o `DataContext` por um shell view model quebraria a tela de hoje e os testes
headless que a sobem.

**Geometria é JSON, não tabela.** `%APPDATA%/MyTaskApp/widget.json`, via
`IWidgetStateStore`. Posição de janela não é dado do usuário, não entra em
backup e não merece migração; um arquivo perdido custa um arrastar de painel.
Arquivo corrompido ou editado à mão degrada para o padrão, e `Sanitized()`
impede painel de tamanho zero.

**Cinco armadilhas, registradas porque falham em silêncio:**

1. **Pixel físico contra unidade de DPI.** `Window.Position` é `PixelPoint`
   físico; `Width`/`Height` são independentes de DPI. Misturar os dois faz o
   painel encolher a cada reabertura num monitor a 150%. A conversão passa pelo
   `Screen.Scaling` da tela onde ele vai pousar.
2. **Posicionar cedo demais não funciona.** Definir `Position` antes de a janela
   ter handle — e mesmo dentro de `OnOpened` — é sobrescrito: o Avalonia ainda
   aplica o próprio posicionamento de abertura depois do evento. A colocação
   acontece num `Dispatcher.UIThread.Post(..., DispatcherPriority.Loaded)`, e
   nada é gravado antes disso (`_placed`), senão o app salvaria a posição de
   cascata do Windows por cima da escolha do usuário.
3. **Seletor de tipo casa exato.** `UserControl.compact Border#CaptureCard` não
   alcança o `TodayView`, que *deriva* de `UserControl` — o seletor precisa do
   tipo concreto (`views|TodayView.compact`). Isto passou pelo compilador e só
   apareceu porque havia teste: o modo compacto simplesmente não escondia a
   captura.
4. **`ElementRole="None"` não é "sem papel": é "invisível ao hit-test do
   chrome".** Os botões do cabeçalho ficam *dentro* do `Border` marcado como
   `TitleBar`. Com `None` o clique não encontra o botão, sobe para a área de
   arrasto e vira movimento de janela — o alfinete só sacode o painel. Quem
   precisa receber clique dentro da barra é `User` (`DecorationsElement` é o
   equivalente, reservado para temas). Esta passou pelo compilador **e pelos
   testes headless**, porque o headless não faz hit-test não-cliente: lá o papel
   não tem efeito nenhum, e um clique simulado passa com os dois valores. Só
   quebra na máquina do usuário. Por isso o teste que a guarda
   (`TheChromeButtons_ReceiveInputInsideTheTitleBar`) afirma o **papel
   declarado**, e não o clique.
5. **`InputHitTest` responde pela árvore de composição, não pelo layout.** Depois
   de trocar de modo, `Bounds` já está atualizado e o hit-test ainda aponta para
   onde os elementos estavam — um teste de clique falha sem que haja nada errado
   no app. Nos testes headless quem fecha essa distância é
   `AvaloniaHeadlessPlatform.ForceRenderTimerTick()`, no `Settle`. Pela mesma
   razão, asserção sobre propriedade com `Transitions` (o `Background` da linha
   tem `BrushTransition` de 120ms) lê um valor no meio da animação: o que vale
   comparar é `GetBaseValue`.

**Ícone é fonte, não emoji.** `📌` e `🔔` vêm com cor própria e ignoram
`Foreground`, o que estoura uma paleta contida. Os glifos vêm de
`Segoe Fluent Icons, Segoe MDL2 Assets` (`WidgetIconFont`), que são
monocromáticos e herdam a cor do botão. A Segoe só existe no Windows; fora dele
quem responde é a fonte embutida do ADR-046.

**Alerta ficou discreto, e do lado certo.** O `AlertWindow` encolheu para 336 e
recebeu a mesma casca do painel. O `AlertPresenter` passou a empilhar na tela
**onde o widget está** (`ScreenFromPoint(MainWindow.Position)`) em vez de sempre
na primária: com dois monitores, o aviso aparecia do outro lado da mesa. O
contador de pendências virou o balão do ícone da bandeja — o aviso mais discreto
que existe, porque só aparece se o usuário for olhar.

**Contratos que a redesenhada teve de preservar** (e que os testes headless
guardam): `Title` continua `"MyTaskApp"`, o `DataContext` continua sendo
`TodayViewModel`, existe exatamente **um** `TextBox` e **um** `CheckBox` por
linha na janela, o botão de captura continua com `Content="Adicionar"`, o rótulo
`HOJE — dd/MM/yyyy` continua desenhado num `TextBlock` (agora como linha de data
do cabeçalho) e o `AlertWindow` continua com exatamente cinco botões e as
classes `card`/`prominent`.

**Limites aceitos:** sem decoração do sistema não há *snap* do Windows (arrastar
para a borda não ancora) — troca consciente por canto arredondado e sombra
próprios. "Iniciar recolhido" esconde a janela no `Opened`, então há um piscar
de um quadro. E o modo compacto redimensiona, mas não lembra: arrastar a borda
vale pela sessão, e a próxima troca de modo volta ao teto de 420px. É de
propósito — `WidgetState` tem um par `Width`/`Height` só, então gravar a altura
do compacto apagaria para sempre o tamanho que o usuário escolheu no painel
inteiro. Nada disso vem do pino, que continua sendo apenas ordem Z.

> **Revisto pelo ADR-048.** O modo discreto saiu, e o alfinete do cabeçalho
> virou "Fixar como HUD", que muda a geometria de propósito. "Sempre no topo"
> continua no menu, só para a janela normal, e continua sendo apenas ordem Z.
> O que este ADR diz sobre o modo discreto e o pino levando-o junto fica como
> registro.
