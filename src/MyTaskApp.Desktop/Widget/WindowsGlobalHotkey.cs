using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;

namespace MyTaskApp.Desktop.Widget;

/// <summary>
/// Uma combinação pelo <c>RegisterHotKey</c> (ADR-048, ADR-054). O aviso chega
/// como <c>WM_HOTKEY</c> na janela principal, que o Avalonia deixa interceptar
/// por <see cref="Win32Properties.AddWndProcHookCallback"/> — sem hook de
/// teclado global: o sistema só avisa quando a combinação inteira é apertada.
/// </summary>
/// <remarks>
/// Uma instância por combinação, cada uma com o seu id: o <c>WM_HOTKEY</c> diz
/// qual disparou, e o gancho de cada uma só responde ao seu.
/// </remarks>
internal sealed partial class WindowsGlobalHotkey(ILogger<WindowsGlobalHotkey> logger, HotkeyGesture gesture)
    : IGlobalHotkeyService
{
    private const uint WmHotkey = 0x0312;

    /// <summary>Segurar a combinação não pode disparar o atalho em rajada.</summary>
    private const uint ModNoRepeat = 0x4000;

    private Window? _owner;
    private nint _hwnd;
    private Win32Properties.CustomWndProcHookCallback? _hook;

    public bool IsSupported => OperatingSystem.IsWindows();

    public string GestureLabel => gesture.Label;

    public bool Register(Window owner, Action pressed)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (_owner is not null)
        {
            return true;
        }

        if (owner.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle)
        {
            return false;
        }

        try
        {
            if (!RegisterHotKey(handle.Handle, gesture.RegistrationId, (uint)gesture.Modifiers | ModNoRepeat, gesture.VirtualKey))
            {
                logger.LogWarning("GlobalHotkeyTaken {Gesture} {Error}", gesture.Id, Marshal.GetLastPInvokeError());
                return false;
            }
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogWarning(exception, "GlobalHotkeyUnavailable");
            return false;
        }

        _hook = (IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == WmHotkey && wParam == gesture.RegistrationId)
            {
                handled = true;
                pressed();
            }

            return IntPtr.Zero;
        };

        Win32Properties.AddWndProcHookCallback(owner, _hook);

        _owner = owner;
        _hwnd = handle.Handle;

        return true;
    }

    public void Unregister()
    {
        if (_owner is null || !OperatingSystem.IsWindows())
        {
            return;
        }

        Release();
    }

    [SupportedOSPlatform("windows")]
    private void Release()
    {
        UnregisterHotKey(_hwnd, gesture.RegistrationId);

        if (_hook is not null)
        {
            Win32Properties.RemoveWndProcHookCallback(_owner!, _hook);
        }

        _owner = null;
        _hook = null;
        _hwnd = 0;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hwnd, int id);
}

/// <summary>Cria um <see cref="WindowsGlobalHotkey"/> por combinação.</summary>
internal sealed class WindowsGlobalHotkeyFactory(ILogger<WindowsGlobalHotkey> logger) : IGlobalHotkeyFactory
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public IGlobalHotkeyService Create(HotkeyGesture gesture) => new WindowsGlobalHotkey(logger, gesture);
}
