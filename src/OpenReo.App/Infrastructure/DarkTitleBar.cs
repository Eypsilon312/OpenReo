using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OpenReo.App.Infrastructure;

/// <summary>
/// Makes the native Windows title bar dark (Windows 10 1809+ / Windows 11) and, on Windows 11,
/// colors it exactly like the application panels.
/// </summary>
public static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Windows 10 1809 – 1909
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;     // Windows 10 2004+ / 11
    private const int DWMWA_BORDER_COLOR = 34;                // Windows 11
    private const int DWMWA_CAPTION_COLOR = 35;               // Windows 11
    private const int DWMWA_TEXT_COLOR = 36;                  // Windows 11

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Applies the dark title bar to every window of the application.</summary>
    public static void RegisterForAllWindows()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Apply((Window)s)));
    }

    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int on = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));

            // Windows 11: exact colors (COLORREF = 0x00BBGGRR). Ignored on Windows 10.
            int caption = 0x00231D1A;   // #1A1D23 (PanelBrush)
            int border = 0x00423530;    // #303542
            int text = 0x00ECE8E6;      // #E6E8EC
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        }
        catch
        {
            // Older Windows versions without dwmapi support: keep the default title bar.
        }
    }
}
