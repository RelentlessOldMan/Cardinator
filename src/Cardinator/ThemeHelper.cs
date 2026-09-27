using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Cardinator;

/// <summary>
/// Gives every window the dark OS title bar (so the chrome matches the app's dark theme instead of the
/// default light bar) and the app icon. Registered once as a class handler so it applies to all windows.
/// </summary>
internal static class ThemeHelper
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;   // Win10 20H1+ / Win11

    private static readonly Lazy<BitmapFrame?> AppIcon = new(LoadIcon);

    private static BitmapFrame? LoadIcon()
    {
        try { return BitmapFrame.Create(new Uri("pack://application:,,,/Assets/cardinator.ico", UriKind.Absolute)); }
        catch { return null; }
    }

    /// <summary>Registers a class handler so every Window gets the dark chrome + icon when it loads.</summary>
    public static void ApplyToAllWindows()
        => EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => Apply((Window)s)));

    public static void Apply(Window window)
    {
        try
        {
            if (AppIcon.Value != null && window.Icon == null) window.Icon = AppIcon.Value;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }
        catch { /* older Windows / no DWM — leave default chrome */ }
    }
}
