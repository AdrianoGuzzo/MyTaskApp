using Microsoft.Maui.Handlers;
using UIKit;

namespace MyTaskApp.Mobile;

/// <summary>
/// O teclado do iPhone sai da frente. Sem isto, depois de aberto ele não
/// fechava: o iOS não tem o "voltar" do Android, o Return da descrição só pula
/// linha, e rolar a tela não o tirava.
/// </summary>
/// <remarks>
/// <para>
/// <b>"OK" em cima do teclado</b>, em todo campo de texto e na busca: o
/// mesmo do Safari e dos Ajustes.
/// </para>
/// <para>
/// <b>Arrastar para baixo fecha</b>, em toda lista e rolagem — exceto o
/// próprio campo de várias linhas, que também rola e precisa do arrasto para
/// selecionar texto.
/// </para>
/// <para>Tocar fora do campo fecha também, pelo <c>HideSoftInputOnTapped</c> das páginas.</para>
/// </remarks>
internal static class KeyboardMappings
{
    private const string DoneBar = "MyTaskAppDoneBar";

    private const string DismissOnDrag = "MyTaskAppDismissOnDrag";

    public static void Apply()
    {
        EntryHandler.Mapper.AppendToMapping(DoneBar, (handler, _) =>
            handler.PlatformView.InputAccessoryView = Toolbar(handler.PlatformView));

        EditorHandler.Mapper.AppendToMapping(DoneBar, (handler, _) =>
            handler.PlatformView.InputAccessoryView = Toolbar(handler.PlatformView));

        SearchBarHandler.Mapper.AppendToMapping(DoneBar, (handler, _) =>
            handler.PlatformView.SearchTextField.InputAccessoryView = Toolbar(handler.PlatformView));

        ViewHandler.ViewMapper.AppendToMapping(DismissOnDrag, (handler, _) =>
        {
            if (handler.PlatformView is UIScrollView scroll and not UITextView)
            {
                scroll.KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive;
            }
        });
    }

    private static UIToolbar Toolbar(UIView field)
    {
        var toolbar = new UIToolbar();
        toolbar.SizeToFit();
        toolbar.Items =
        [
            new UIBarButtonItem(UIBarButtonSystemItem.FlexibleSpace),
            new UIBarButtonItem(UIBarButtonSystemItem.Done, (_, _) => field.EndEditing(true)),
        ];

        return toolbar;
    }
}
