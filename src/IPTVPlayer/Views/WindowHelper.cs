using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IPTVPlayer.Views;

internal static class WindowHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>Barra de título escura no Windows 10 (20H1+) e 11.</summary>
    public static void UseDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            var enabled = 1;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch
        {
            // Versões antigas do Windows: fica a barra clara.
        }
    }

    /// <summary>Tempo de duplo clique configurado no Windows.</summary>
    public static int DoubleClickTime
    {
        get
        {
            try
            {
                return (int)GetDoubleClickTime();
            }
            catch
            {
                return 500;
            }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}
