using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Views;

public sealed partial class TagsWindow : Window
{
    public TagsWindow() => InitializeComponent();

    public TagsWindow(TagsViewModel viewModel)
        : this() => DataContext = viewModel;

    /// <summary>
    /// O "X" esconde, não fecha: a janela é um singleton do contêiner, e uma
    /// janela do Avalonia fechada de verdade não pode ser mostrada de novo (ver
    /// <see cref="DataManagementWindow"/>). Encerrar o app fecha por outro
    /// motivo, e esse passa.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Recarrega a cada abertura — a contagem de uso muda enquanto a janela
    /// está escondida — e já põe o cursor no nome, que é por onde se começa.
    /// </summary>
    public void Reveal()
    {
        if (DataContext is TagsViewModel viewModel)
        {
            _ = viewModel.LoadAsync(CancellationToken.None);
        }

        NameBox.Focus();
    }

    /// <summary>
    /// "Onde aparece…" da etiqueta (ADR-062): um item por lugar, com o atual
    /// marcado, montado na hora com os computadores da conta.
    /// </summary>
    private void OnPlacementClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TagListItemViewModel item } anchor
            || DataContext is not TagsViewModel viewModel)
        {
            return;
        }

        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

        foreach (var choice in viewModel.PlacementChoices(item))
        {
            menu.Items.Add(new MenuItem
            {
                Header = choice.Option.Label,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = choice.Option.IsCurrent,
                Command = viewModel.PlaceTagCommand,
                CommandParameter = choice,
            });
        }

        menu.ShowAt(anchor);
    }

    /// <summary>
    /// "Importar de outro computador…" (ADR-077): uma linha por etiqueta que
    /// está só em outro computador, com a cor dela e de onde vem.
    /// </summary>
    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control anchor && DataContext is TagsViewModel viewModel)
        {
            BuildImportMenu(viewModel).ShowAt(anchor);
        }
    }

    internal static MenuFlyout BuildImportMenu(TagsViewModel viewModel)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

        foreach (var choice in viewModel.ImportableTags)
        {
            menu.Items.Add(new MenuItem
            {
                Header = choice.Label,
                Icon = new Border
                {
                    Width = 12,
                    Height = 12,
                    CornerRadius = new CornerRadius(3),
                    Background = new ImmutableSolidColorBrush(Color.Parse(choice.ColorHex)),
                },
                Command = viewModel.ImportTagCommand,
                CommandParameter = choice,
            });
        }

        return menu;
    }

    /// <summary>
    /// "Procurar…" do diretório (ADR-026). Fica aqui porque o seletor de pastas é
    /// serviço da janela; o que fazer com a pasta escolhida está no ViewModel.
    /// </summary>
    private async void OnBrowseFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TagListItemViewModel item })
        {
            return;
        }

        var path = await SystemPickers.PickFolderAsync(
            StorageProvider,
            "Escolher a pasta do diretório",
            message =>
            {
                if (DataContext is TagsViewModel viewModel)
                {
                    viewModel.ErrorMessage = message;
                }
            });

        if (path is not null && DataContext is TagsViewModel owner)
        {
            await owner.UseFolderAsync(item, path, CancellationToken.None);
        }
    }

    /// <summary>
    /// "Outra pasta…" do clone (ADR-064): escolhe a pasta-mãe, e o repositório
    /// entra numa subpasta com o nome dele.
    /// </summary>
    private async void OnBrowseCloneFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TagListItemViewModel item })
        {
            return;
        }

        var parent = await SystemPickers.PickFolderAsync(
            StorageProvider,
            "Escolher onde clonar o repositório",
            message =>
            {
                if (DataContext is TagsViewModel viewModel)
                {
                    viewModel.ErrorMessage = message;
                }
            });

        if (parent is not null)
        {
            item.UseCloneFolder(parent);
        }
    }
}
