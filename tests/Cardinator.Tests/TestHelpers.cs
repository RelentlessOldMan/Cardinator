using System.Threading;
using System.Windows.Media.Imaging;

namespace Cardinator.Tests;

/// <summary>Shared helpers for render tests (STA thread + pixel checks).</summary>
internal static class TestHelpers
{
    /// <summary>Runs an action on a dedicated STA thread (required by RenderTargetBitmap).</summary>
    public static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null) throw captured;
    }

    /// <summary>
    /// Lets go of everything a window test built on this thread: closes its windows and shuts its Dispatcher down.
    /// Both hold a MainWindow (its frames, renders and undo history) alive after the thread ends, so without this
    /// every window test's ~200 MB stayed for the whole run and the window tests alone grew the test process to
    /// ~9.5 GB (14 GB for the suite — near the 16 GB of a CI runner). Measured: closing alone or shutting down alone
    /// changes nothing; both together keep the window tests under 1 GB.
    /// <list type="bullet">
    /// <item>WPF's <see cref="System.Windows.Application"/> keeps every window until it is closed, even one never shown on
    /// a finished thread (in the internal NonAppWindowsInternal list, hence the reflection).</item>
    /// <item>A Dispatcher that's never shut down keeps its hidden message window, which keeps the Dispatcher — and the
    /// timers MainWindow started on it, and through them the window.</item>
    /// </list>
    /// </summary>
    public static void EndUiThread()
    {
        var app = System.Windows.Application.Current;
        var mine = new List<System.Windows.Window>();
        foreach (var name in app == null ? Array.Empty<string>() : new[] { "NonAppWindowsInternal", "WindowsInternal" })
        {
            var list = typeof(System.Windows.Application)
                .GetProperty(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                .GetValue(app) as System.Windows.WindowCollection;
            if (list == null) continue;
            lock (list) mine.AddRange(list.Cast<System.Windows.Window>().Where(w => w.Dispatcher.CheckAccess()));
        }
        foreach (var w in mine)
        {
            if (w is Cardinator.MainWindow m) m.SuppressClosePrompt = true;   // never ask "save changes?" on the way out
            try { w.Close(); } catch (InvalidOperationException) { /* already closing */ }
        }
        System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
    }

    /// <summary>A real PNG (a solid w×h block of <paramref name="argb"/>, default opaque blue) as bytes.</summary>
    public static byte[] PngBytes(int w, int h, uint argb = 0xFF2850A0)
    {
        int stride = w * 4;
        var px = new byte[h * stride];
        for (int i = 0; i < px.Length; i += 4)
        { px[i] = (byte)argb; px[i + 1] = (byte)(argb >> 8); px[i + 2] = (byte)(argb >> 16); px[i + 3] = (byte)(argb >> 24); }
        var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, stride);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new System.IO.MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static byte[] Pixels(BitmapSource bmp)
    {
        int stride = bmp.PixelWidth * 4;
        var pixels = new byte[bmp.PixelHeight * stride];
        bmp.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>True if the bitmap has any visible, non-white pixel (i.e. something was drawn).</summary>
    public static bool HasContent(BitmapSource bmp)
    {
        var pixels = Pixels(bmp);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
            if (a > 10 && (r < 200 || g < 200 || b < 200)) return true;
        }
        return false;
    }
}
