# ADR-041 — Temas: sete paletas, "Automático" segue o Windows

| | |
|---|---|
| **Status** | Aceito |
| **Implementação** | Implementado |
| **Categoria** | ui-desktop |
| **Registrado** | 2026-09-28, commit `0533a3c` (primeiro commit em que o ADR aparece) |
| **Palavras-chave** | ThemeCatalog, ThemeController, ThemeResources, Automático, alto contraste, WCAG, DynamicResource, Verde Pecuária |
| **Relações** | revisa: [ADR-017](017-janela-principal-como-widget.md); relacionado: [ADR-053](053-historico-sete-dias-projecao.md), [ADR-054](054-post-it-entidade-propria.md), [ADR-075](075-celular-temas-concluir-e-teclado.md) |
| **Código** | `src/MyTaskApp.Desktop/Theming/ThemeCatalog.cs`, `src/MyTaskApp.Desktop/Theming/ThemeController.cs`, `src/MyTaskApp.Desktop/Theming/ThemeResources.cs`, `src/MyTaskApp.Desktop/Widget/WidgetState.cs` |

**Contexto:** o ADR-017 fixou o app no escuro (`RequestedThemeVariant="Dark"`)
porque seguir o Windows, naquela época, dava uma chapa preta de 900x700. O
problema era o tamanho e o preto puro, não a ideia de seguir o sistema. Agora o
painel é um widget com paleta própria, e dá para oferecer o claro sem voltar
ao problema antigo.

**Decisão:** sete temas, e mais "Automático", que é o padrão:

| Id              | Nome            | Tipo   | Destaque              |
|-----------------|-----------------|--------|-----------------------|
| `paper`         | Papel           | claro  | índigo                |
| `sepia`         | Sépia           | claro  | petróleo              |
| `charcoal`      | Carvão          | escuro | índigo (o de sempre)  |
| `nordic`        | Nórdico         | escuro | gelo                  |
| `plum`          | Ameixa          | escuro | magenta               |
| `ponta`         | Verde Pecuária  | escuro | verde-limão           |
| `high-contrast` | Alto contraste  | escuro | amarelo               |

"Automático" (`system`) usa Papel com o Windows claro, Carvão com o Windows
escuro e Alto contraste quando um tema de contraste do Windows está ligado
(`PlatformColorValues.ContrastPreference`). A troca no sistema vale na hora,
por `IPlatformSettings.ColorValuesChanged`. Um tema escolhido à mão ganha de
tudo, inclusive do alto contraste do sistema: quem escolheu já viu como fica.

A escolha fica em `widget.json` (`WidgetState.Theme`), como texto e não como
enum, para um tema novo não exigir migração. Um id desconhecido, de tema
removido ou de arquivo editado à mão, volta a ser `system`. Quem atualiza tem
um arquivo sem a chave e passa a seguir o Windows. Com o Windows escuro, isso
dá o mesmo Carvão de antes.

**A paleta é C#, e não um `.axaml` por tema.** `Theming/ThemeCatalog.cs` tem
as cores. `ThemeResources.Build` monta um `ResourceDictionary` com os pares
`Widget{Nome}Color`/`Widget{Nome}Brush` e as chaves do Fluent que o app
reaponta. `ThemeController` troca esse dicionário dentro de
`Application.Resources.MergedDictionaries`, no mesmo lugar, e ajusta
`RequestedThemeVariant` para o Fluent pintar o que a paleta não cobre (barra de
título das janelas auxiliares, rolagem, seletor de cor). Em C#, o teste de
contraste lê a mesma paleta que a tela desenha, sem subir janela.
`Tokens.axaml` ficou só com o que não muda entre temas: raios, fonte de
ícones e as transparências de propósito.

**Toda cor de tema é `DynamicResource`.** Um `StaticResource` resolve uma vez,
na carga, e prende a tela ao tema que estava ativo quando ela abriu.
`ThemeResourcesTests` varre os `.axaml` e falha se uma tela congela uma chave
`Widget*`, ou se pede uma chave que algum tema não define. Os hex que ainda
estavam soltos (sombra do painel e dos alertas, fundo dos botões do modo
discreto, texto do botão de perigo, o visto branco da marca e do Markdown)
viraram tokens: `WidgetShellShadow`, `WidgetPopupShadow`,
`WidgetGhostChromeBrush`, `WidgetOnAccentBrush`.

**Contraste medido, e não a olho** (`ThemeContrastTests`, WCAG 2.2):

- texto principal: 7:1 em fundo, cartão e hover (AAA, 1.4.6);
- texto de apoio, inclusive o nível mais baixo, onde fica a data de 10px:
  4,5:1 (AA, 1.4.3);
- sinalização (perigo, atenção, informação, sucesso): 4,5:1, porque vira
  texto ("atrasada", o link da anotação);
- destaque como forma (ícone ligado, barra, checkbox): 3:1 (1.4.11);
- letra sobre o botão de destaque, nos três estados: 4,5:1;
- no Alto contraste, as bordas também passam de 3:1.

O Carvão não passava. A letra branca sobre `#6366F1` dava 4,47:1, e o
`TextLow` `#6B7180` dava 3,6:1. Mudaram para `#5F63EF`, que visualmente é a
mesma cor, e `#838996`. O hover do destaque no escuro agora fecha em vez de
abrir, como no Fluent, porque abrir tirava a letra branca do mínimo.

**`AccentText` separado de `Accent`.** Num tema escuro com letra branca no
botão, a mesma cor não chega a 4,5:1 contra o fundo e contra o branco ao mesmo
tempo. Pela fórmula, a luminância precisaria ser ≥ 0,233 e ≤ 0,183 ao mesmo
tempo. Então o destaque usado como **texto** (`WidgetAccentTextBrush`: rótulos de
código, "ok", o chip do agente) é outra cor, e o destaque como preenchimento
continua sendo `WidgetAccentBrush`.

**Semântica fixa por matiz.** Vermelho é perigo, âmbar é atenção, azul é
informação e verde é sucesso, em todos os temas. Por isso nenhum destaque usa
essas matizes: Sépia é petróleo, Ameixa é magenta (rosa ficaria perto do
perigo), Nórdico é gelo. No Alto contraste o destaque é amarelo, e a atenção
virou laranja.

**Verde Pecuária, a exceção de marca.** O Verde Pecuária veio depois, na
identidade verde da Ponta, e é o único tema com destaque na matiz do sucesso.
Chamava-se "Ponta"; o id `ponta` ficou, porque é o que está gravado no
`widget.json` de quem já o escolheu, e trocar o id mandaria essa pessoa de
volta para "Automático". A paleta da marca vai
para os papéis de sempre, sem chave nova: petróleo `#104544` no fundo, `#154E44`
no cartão, `#1E5B45` no hover, `#2D6F44` na borda, e o limão `#7CBB3B` só na
ação (`#A2D152` no hover e como texto, `#5EA341` pressionado). Para o verde não
virar duas coisas, o sucesso é menta (`#6EE7B7`), a mais de 40° de matiz do
limão (`ThemeCatalogTests`). Dois tons saíram da paleta por contraste: a letra
do botão é `#0A2F2E`, porque `#104544` sobre o pressionado dava 3,5:1, e o
`AccentSoft` é `#24573A`, porque o limão como texto sobre o teal `#1E5B45` dava
4,47:1.

**Armadilhas do Fluent encontradas renderizando, e não nos testes:** várias
chaves do Fluent apontam para outras por `StaticResource` dentro do próprio
dicionário do tema. Reapontar `TextOnAccentFillColorPrimaryBrush` não chega ao
visto da checkbox (`CheckBoxCheckGlyphForeground*`), ao texto do botão
`accent` (`AccentButtonForeground*`) nem ao `ToggleButton` ligado. No Nórdico o
"Abrir" saía branco sobre gelo, e no Alto contraste o visto saía branco sobre
amarelo. Essas chaves são reapontadas uma a uma. O `Button` comum usa
`ButtonBackground`, que é branco ou preto translúcido, e não
`ControlFillColorDefault`. Ele passou a usar o `TextHigh` do tema translúcido:
no Sépia era cinza frio sobre creme, e no Carvão 20% de quase-branco é o que
já era.

A seleção de texto (`TextBox`, `SelectableTextBlock` das anotações) é pintada
com o destaque, mas o Fluent deixa a letra na cor do texto. Com destaque claro
(Nórdico, Ameixa, Alto contraste, Verde Pecuária), o selecionado ficava branco sobre
claro, perto de 2:1. Agora a letra selecionada é `WidgetOnAccentBrush`
(`Styles/Widget.axaml`). Nos temas de letra branca no botão, nada muda.

**A escolha no menu.** "Tema ▸" no menu do painel, com "Automático" primeiro.
Cada item tem uma amostra nas cores do tema (fundo, cartão e um ponto de
destaque; o "Automático" é meio Papel, meio Carvão) e a descrição no balão. É
rádio de verdade (`ToggleType="Radio"`), então o leitor de tela anuncia qual
está marcado. O item tem `StaysOpenOnClick`: dá para passar pelos temas e ver
cada um aplicado no painel sem reabrir o menu.

**Limites aceitos:**

- a cor das etiquetas é do usuário e não muda com o tema. Uma etiqueta amarela
  clara num tema claro tem pouco contraste como bolinha. O texto sobre ela
  continua certo (`TagColor.PrefersDarkText`);
- o destaque não segue a cor de destaque do Windows. Cada tema foi validado com
  o próprio destaque, e uma cor arbitrária do sistema quebraria os mínimos
  acima.
