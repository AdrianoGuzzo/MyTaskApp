# Janela, HUD, bandeja e temas

| | |
|---|---|
| **Status** | Implementado no Windows. No Linux faltam o clique que atravessa o HUD, os atalhos globais e o início automático. |
| **Decisões** | [ADR-016](../architecture/decisions/016-bandeja-e-fechar-para-esconder.md) bandeja · [ADR-017](../architecture/decisions/017-janela-principal-como-widget.md) widget · [ADR-019](../architecture/decisions/019-instancia-unica-por-mutex-nomeado.md) instância única · [ADR-023](../architecture/decisions/023-iniciar-com-windows-chave-run.md) iniciar com o Windows · [ADR-041](../architecture/decisions/041-temas-sete-paletas-e-automatico.md) temas · [ADR-046](../architecture/decisions/046-fonte-de-icones-embutida.md) fonte de ícones · [ADR-048](../architecture/decisions/048-hud-visivel-sem-atrapalhar.md) HUD |
| **Código** | `src/MyTaskApp.Desktop/Widget/`, `src/MyTaskApp.Desktop/ViewModels/WidgetChromeViewModel.cs`, `src/MyTaskApp.Desktop/Views/MainWindow.axaml`, `src/MyTaskApp.Desktop/Theming/`, `src/MyTaskApp.Desktop/Composition/SingleInstance.cs`, `src/MyTaskApp.Desktop/Composition/WindowsStartupRegistration.cs` |
| **Testes** | `tests/MyTaskApp.Desktop.Tests/Widget/`, `tests/MyTaskApp.Desktop.Tests/Theming/ThemeCatalogTests.cs`, `tests/MyTaskApp.Desktop.Tests/Composition/SingleInstanceTests.cs` |

- Painel de 360×560 sem moldura, arrastável e redimensionável, com três modos:
  **Painel completo**, **Modo compacto** e **Recolher**. A posição e o tamanho
  ficam salvos (`widget.json`).
- **Modo HUD** (o alfinete do cabeçalho). O painel vira um cartão compacto,
  sempre visível sobre as outras janelas num canto da tela, só com o que falta
  fazer. Fora do cartão, o clique vai para o app de trás. Posição (seis cantos ou
  onde você arrastar), tamanho (Compacto, Normal, Expandido), opacidade do fundo
  e "HUD recolhido" (uma pílula que abre ao passar o mouse) ficam em
  *☰ → Janela e comportamento…*. Para voltar, clique no alfinete aceso.
- **Sempre no topo** (no menu) vale para a janela normal e não muda posição nem
  tamanho.
- **Ao fechar a janela:** fechar o aplicativo, minimizar para a bandeja (o
  padrão; os lembretes continuam rodando) ou entrar no modo HUD. Dá para
  **iniciar no modo HUD** e, no Windows, ligar **Ctrl+Shift+Espaço** para
  alternar entre janela e HUD. O atalho vem desligado porque o Visual Studio usa
  a mesma combinação.
- **Instância única.** Abrir o atalho de novo traz a janela que já existe e não
  cria um segundo processo (mutex `MyTaskApp.SingleInstance`, por sessão do
  Windows; o instalador usa o mesmo nome).
- **Iniciar com o Windows** (ligado por padrão no instalador). O app sobe
  direto na bandeja (`--startup`). Dá para mudar em *☰ → Iniciar com o Windows*.
- **Temas:** Papel, Sépia, Carvão, Nórdico, Ameixa, Verde Pecuária e Alto
  contraste, além de **Automático**, que segue o tema claro, escuro ou de alto
  contraste do Windows.
- Na última linha do menu ☰ fica a versão instalada (`MyTaskApp X.Y.Z · <commit> · <data>`);
  clicar copia esses dados para um relato de problema
  ([verificar uma versão](../release-process.md#8-verificar-uma-versão-instalada)).
