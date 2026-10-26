# ADR-048 — HUD: o pino deixa de ser "transparente" e vira "visível sem atrapalhar"

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ui-desktop |
| **Registrado** | 2026-10-03, commit `c4d1101` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | HUD, WindowMode, SetWindowRgn, CloseBehavior, HudPlacement, HUD recolhido, atalho global, modo discreto |
| **Relações** | revisa: [ADR-017](017-janela-principal-como-widget.md); exceção a: [ADR-023](023-iniciar-com-windows-chave-run.md); relacionado: [ADR-016](016-bandeja-e-fechar-para-esconder.md), [ADR-054](054-post-it-entidade-propria.md) |
| **Código** | `src/MyTaskApp.Desktop/Widget/WindowMode.cs`, `src/MyTaskApp.Desktop/Widget/CloseBehavior.cs`, `src/MyTaskApp.Desktop/Widget/HudPlacement.cs`, `src/MyTaskApp.Desktop/Widget/WindowsWindowBehavior.cs` |

**Contexto:** o pino do ADR-017 ligava `Topmost` e o modo discreto num clique
só. O modo discreto apagava o fundo do painel (`Background="Transparent"`) e o
ADR registrava o custo: o retângulo continuava capturando cliques. Na prática o
painel ficava difícil de enxergar, a área "vazia" bloqueava o app de trás, e
sair do modo exigia achar um alfinete flutuante ou a bandeja. Um booleano
(`IsPinned`) controlava três coisas por baixo.

**Decisão:** dois conceitos de janela, uma máquina de estados explícita e
preferências independentes.

- `WindowMode { Normal, Hud, HudCollapsed }` é o único estado que decide a
  forma da janela. Só os comandos da moldura o movem: `EnterHud`, `ExitHud`,
  `ToggleHud`, `CollapseHud`, `ExpandHud`. Recolher e expandir não valem fora do
  HUD; da pílula também se sai direto para a janela normal.
- **Separado disso**, cada um com o seu teste: "sempre no topo" da janela
  normal (`Topmost`), "manter o HUD sempre visível" (`Hud.AlwaysOnTop`), a
  opacidade do fundo do HUD (`Hud.Opacity`) e a região clicável (que não é
  preferência: é a forma da janela). `IsWindowTopmost` escolhe entre os dois
  "no topo" conforme o modo. Sair do HUD nunca deixa a janela grande presa na
  frente de tudo.
- O modo discreto **saiu**. O alfinete do cabeçalho virou "Fixar como HUD"; no
  HUD ele aparece aceso, e o clique volta à janela normal. "Sempre no topo"
  continua no menu, e continua sem tocar em geometria (o que o ADR-017 garante
  segue garantido por `WidgetPinTests`).

**A área transparente não bloqueia o mouse porque não existe.** No Windows,
pixel transparente não deixa o clique passar: quem decide é o retângulo da
janela (`WM_NCHITTEST`). Por isso o HUD não é "o painel com fundo apagado", e
sim uma janela do tamanho exato do cartão: sem o anel de 10px da sombra
(`Border.widgetShell.hud` zera margem e sombra), largura fixa por preset e
altura do conteúdo (`SizeToContent.Height` com `MaxHeight` do preset). Um HUD
com uma tarefa não ocupa a altura de dez, e cada pixel que ele não ocupa é
clique que chega no app de trás. Isso vale em qualquer plataforma, sem código
nativo.

No Windows, `IWindowBehaviorService.SetInteractiveRegion` fecha o que sobra:
`SetWindowRgn` com o retângulo arredondado do cartão, para os cantos também
repassarem o clique. Conferido numa janela real (não no headless):
`WindowFromPoint` no centro do HUD devolve a janela do app; no canto arredondado
e fora do cartão, a janela de trás. Ao voltar para a janela normal a região é
removida. Duas alternativas descartadas:

- `WS_EX_LAYERED | WS_EX_TRANSPARENT` torna a janela **inteira** transparente
  ao mouse. Alterná-lo pela posição do cursor exigiria ler o ponteiro global
  num timer — exatamente o "capturar o mouse" que o HUD não pode fazer.
- `HTTRANSPARENT` no `WM_NCHITTEST` (o `Win32Properties.NonClientHitTestResult`
  do Avalonia 12) só repassa o clique para janelas da **mesma thread**, então
  não serve para o VS Code, o terminal ou o navegador.

**Legibilidade antes de discrição.** O fundo do HUD é uma camada própria
(`Border.hudBackdrop`) com o pincel opaco do tema e a opacidade escolhida. O
texto fica em outra camada e nunca fica translúcido. O piso é 70%
(`HudSettings.MinOpacity`), e `Sanitized()` o impõe também a um `widget.json`
editado à mão. A borda do cartão fica (1px), para ele se separar de qualquer
fundo. No HUD a lista mostra só o que falta (`HideCompleted`, herdado do pino
antigo), e as quatro ações discretas da linha, que reservavam ~100px mesmo
invisíveis, saem do layout: anotação e menu voltam no hover; etiqueta e
lembrete ficam para a janela normal.

**Posição: canto da tela onde a janela está, na escala dessa tela.**
`HudPlacement.Resolve` é função pura sobre retângulos em pixels físicos, como o
`WidgetPlacement`. Ela faz o papel do `IHudPositionService` pedido; virou
estática porque não tem estado nem dependência. Seis cantos/bordas e
"Personalizada". A margem (12 DIP) é escalada pela tela de destino. A tela é a
do centro da janela normal no momento de entrar no HUD, e não sempre a
principal. Reposicionar a cada mudança de altura é o que faz um HUD ancorado
embaixo crescer para cima.

**Arrastar é escolha; o sistema mover não é.** O cabeçalho do HUD não tem
`ElementRole="TitleBar"`: o arrasto é iniciado pelo próprio painel
(`BeginMoveDrag`), e só depois disso um `PositionChanged` vira "posição
personalizada" (com 400ms de silêncio para dar o arrasto por encerrado). Uma
troca de DPI ou um monitor desligado também movem a janela e, sem essa
distinção, transformariam "superior esquerdo" em "personalizada" sem ninguém
pedir.

**A janela normal volta exatamente para onde estava.** Ao entrar no HUD, a
geometria normal é carimbada em `_state`, e enquanto o HUD estiver na tela o
`Capture()` não grava posição nem tamanho: o `widget.json` guarda a janela
normal, e `Hud.X/Y` guarda o arrasto do HUD. Ao sair, `RestoreNormalPosition`
põe o canto superior esquerdo de volta, e `ApplyMode` repõe o tamanho do modo.

**O X é configurável, e nunca ignorado.** `CloseBehavior { Exit, Tray, Hud }`,
padrão `Tray` (o que o X sempre fez, ADR-016). `CloseRouting.Decide` é pura;
`MainWindow.OnClosing` só executa. O X do cabeçalho, o Alt+F4 e o "fechar
janela" da barra de tarefas passam por ali:

| Escolha | Janela normal | HUD |
|---|---|---|
| Fechar o aplicativo | sai (pelo encerramento do App) | sai |
| Minimizar para a bandeja | esconde | esconde |
| Entrar no modo HUD | vira HUD | **pergunta**: voltar à janela normal, ocultar na bandeja ou sair |

Sem ícone na bandeja, "esconder" vira "sair", e o item "Ocultar na bandeja"
some dos menus: sem ícone, não haveria caminho de volta. Desligar o Windows e
encerrar o app (`ApplicationShutdown`, `OSShutdown`) não perguntam nada.
"Sair do MyTaskApp" entrou no menu do painel e no do HUD, pelo mesmo
encerramento da bandeja (`ExitHandler`).

**HUD recolhido.** O HUD pode virar uma pílula (`✓ MyTaskApp` e o número de
pendências) pelo menu. Com "Usar HUD recolhido" ligado, ele entra assim e volta
à pílula 1,2s depois de o mouse sair. A pílula abre com o clique, ou com o
mouse parado 350ms sobre ela, para que atravessar a pílula a caminho de outra
coisa não abra nada. Não recolhe com a captura aberta, com o aviso da primeira
vez à vista ou com um menu aberto. Os menus são outra janela, então o ponteiro
"sai" do HUD ao entrar neles, e um conjunto estático de `Popup` abertos cobre
isso.

**Atalho global: existe, mas nasce desligado.** `IGlobalHotkeyService`, no
Windows com `RegisterHotKey` + `Win32Properties.AddWndProcHookCallback`, sem
hook de teclado. Ctrl+Shift+Espaço alterna janela e HUD, e revela o app se ele
estiver na bandeja. Desligado por padrão porque, no Visual Studio e no VS Code,
a mesma combinação é "informações de parâmetro", e um atalho global a roubaria
sem aviso. A opção diz isso na tela. Se outro programa já for dono da
combinação, a caixa desmarca e a tela explica o motivo. Fora do Windows a opção
some (no Wayland não há atalho global por desenho; no X11 fica para quando
alguém pedir).

**Iniciar no HUD.** "Iniciar o MyTaskApp no modo HUD" ganha de como a janela
estava ao fechar; sem ele, o app reabre no modo em que foi deixado
(`WidgetState.WindowMode`). É a exceção ao "o login do Windows sobe direto na
bandeja" (ADR-023): quem pediu o painel permanente quer vê-lo depois do login,
e o HUD é justamente a forma que não aparece na frente de ninguém.

**Descoberta.** Na primeira vez no HUD, um aviso dentro do próprio cartão diz o
que ele é, onde se ajusta e oferece "Entendi" (`Hud.IntroSeen`). Dentro do
cartão, e não numa janela por cima: quem acabou de fixar o app quer ver onde
ele foi parar.

**Configuração:** "Janela e comportamento…" (menu do painel, do HUD e da
bandeja) liga direto na moldura (`WidgetChromeViewModel`), então cada escolha
vale na hora e vai para o `widget.json` pelo mesmo caminho do resto, sem
segundo mecanismo e sem botão "Salvar". O arquivo ganhou `WindowMode`,
`CloseBehavior`, `StartInHud`, `GlobalHotkey` e um bloco `Hud` aninhado. Um
arquivo antigo abre como antes: X esconde na bandeja e a janela abre normal. A
exceção é quem atualiza **com o pino ligado** (`Topmost` e `Ghost`): reabre no
HUD, porque reabrir como janela grande fixada no topo seria o pior dos dois. O
campo `Ghost` só é lido, nunca mais gravado.

**Animação:** 150ms de opacidade ao entrar, sair, recolher e abrir, e nada mais.
Tamanho de janela não anima.

**Limites aceitos:**

- fora do Windows não há recorte de região. Sobram os cantos arredondados do
  cartão (alguns pixels) capturando clique. Não há margem transparente em
  plataforma nenhuma;
- a região do `SetWindowRgn` é serrilhada (GDI não faz antialiasing). Nos 10px
  de raio quase não se vê;
- o HUD não redimensiona com o mouse: o tamanho é escolha de preset
  (Compacto, Normal, Expandido);
- no X11 o `BeginMoveDrag` volta antes do fim do arrasto. Uma pausa de mais de
  400ms no meio do arrasto pode encerrar o registro da posição antes da hora. O
  próximo arrasto corrige.

**Testes:** `WindowModeTests` (transições e independência dos conceitos),
`CloseRoutingTests`, `HudPlacementTests` (cantos, monitor secundário com
coordenada negativa, DPI, ponto personalizado num monitor que sumiu),
`WidgetStateStoreTests` (ida e volta, migração do pino antigo, arquivo editado à
mão) e `WidgetHudTests` (headless: cartão sem anel transparente, altura que
acompanha o conteúdo, volta exata da janela normal, o X em cada combinação, a
região pedida ao serviço, aviso da primeira vez).
