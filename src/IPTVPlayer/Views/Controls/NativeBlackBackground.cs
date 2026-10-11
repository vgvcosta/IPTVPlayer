using System.Runtime.InteropServices;

namespace IPTVPlayer.Views.Controls;

/// <summary>
/// A janela nativa onde o VLC desenha mostra a cor de sistema (cinzento-claro) quando não há vídeo
/// (erro, parado). Fazemos subclass dela para pintar o fundo de preto.
/// </summary>
internal static class NativeBlackBackground
{
    private const uint WmPaint = 0x000F;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmNcDestroy = 0x0082;
    private const int BlackBrush = 4;
    private static readonly UIntPtr SubclassId = 1;

    // Referência estática: o delegate não pode ser recolhido pelo GC enquanto a janela existir.
    private static readonly SubclassProc Proc = WndProc;
    private static readonly IntPtr Brush = GetStockObject(BlackBrush);

    public static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (SetWindowSubclass(hwnd, Proc, SubclassId, UIntPtr.Zero))
            InvalidateRect(hwnd, IntPtr.Zero, true);
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData)
    {
        switch (msg)
        {
            case WmEraseBkgnd:
                GetClientRect(hWnd, out var rect);
                FillRect(wParam, ref rect, Brush);
                return 1;

            case WmPaint:
                var hdc = BeginPaint(hWnd, out var ps);
                FillRect(hdc, ref ps.rcPaint, Brush);
                EndPaint(hWnd, ref ps);
                return IntPtr.Zero;

            case WmNcDestroy:
                RemoveWindowSubclass(hWnd, Proc, SubclassId);
                break;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr hdc;
        public bool fErase;
        public Rect rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, UIntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int obj);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PaintStruct ps);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, ref PaintStruct ps);
}
