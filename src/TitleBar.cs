using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ComfyNodeUpdater;

/// <summary>
/// Paints the native title bar in the theme's colours. The immersive-dark flag alone does not revert
/// to a light caption, so the caption, text and border colours are set explicitly (Windows 11).
/// </summary>
public static class TitleBar
{
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private const int ImmersiveDark = 20;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var theme = Themes.Current;

        var dark = theme.Dark ? 1 : 0;
        DwmSetWindowAttribute(handle, ImmersiveDark, ref dark, sizeof(int));

        var caption = Bgr(Themes.Color(theme.Card));
        DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));

        var text = Bgr(Themes.Color(theme.Text));
        DwmSetWindowAttribute(handle, TextColor, ref text, sizeof(int));

        var border = Bgr(Themes.Color(theme.Line));
        DwmSetWindowAttribute(handle, BorderColor, ref border, sizeof(int));

        // the non-client area only repaints on a frame change
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpFrameChanged);
    }

    /// <summary>DWM wants 0x00bbggrr, not the #rrggbb the themes store.</summary>
    private static int Bgr(Color color) => color.R | (color.G << 8) | (color.B << 16);
}
