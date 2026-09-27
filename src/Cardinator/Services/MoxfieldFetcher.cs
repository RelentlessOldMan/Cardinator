using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Cardinator.Services;

/// <summary>Raised when a Moxfield deck can't be fetched; the message is safe to show the user.</summary>
public sealed class MoxfieldException : Exception
{
    public MoxfieldException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Fetches a Moxfield deck's JSON using a hidden Edge/WebView2 browser engine. Moxfield's API is behind
/// Cloudflare bot protection that 403s plain HTTP clients (it fingerprints the TLS/HTTP stack, not just
/// the User-Agent), but a real browser engine — with a real fingerprint and Cloudflare's clearance
/// cookie — is let through, exactly like the user's own browser. The WebView2 window is created
/// off-screen at 1×1 and never activated, so nothing visible pops up.
/// </summary>
public static class MoxfieldFetcher
{
    private static string UserDataFolder => Path.Combine(AppPaths.DataDir, "webview2");

    /// <summary>
    /// Loads the deck's JSON from Moxfield. Must be called on the UI thread. Throws
    /// <see cref="MoxfieldException"/> with a user-safe message on any failure (missing runtime,
    /// Cloudflare block, timeout, cancellation) so the caller can fall back to the paste flow.
    /// </summary>
    public static async Task<string> FetchDeckJsonAsync(
        string deckId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var apiUrl = MoxfieldClient.ApiUrl(deckId);

        // A 1×1 off-screen, non-activating host window: WebView2 needs a live HWND, but the user
        // never sees or loses focus to it.
        var host = new Window
        {
            Width = 1, Height = 1, Left = -4000, Top = -4000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Focusable = false, Opacity = 0,
        };
        var web = new WebView2();
        host.Content = web;

        try
        {
            host.Show();

            CoreWebView2Environment env;
            try
            {
                env = await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataFolder);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                throw new MoxfieldException(
                    "Couldn't start the embedded browser (WebView2). Install the Microsoft Edge WebView2 "
                    + "runtime, or use the pasted deck-list import instead.", ex);
            }

            var core = web.CoreWebView2;
            progress?.Report("Contacting Moxfield…");

            // Navigate to the API endpoint so we end up on its origin; Cloudflare clears the real browser
            // (auto-solving any managed challenge), then a same-origin fetch returns the raw JSON reliably.
            await NavigateAsync(core, apiUrl, ct);

            // Cloudflare's managed challenge can take a couple of seconds to auto-clear; retry the
            // same-origin fetch a few times before giving up.
            const string fetchScript = @"(async () => {
                try {
                    const r = await fetch(location.href, { credentials:'include', headers:{'Accept':'application/json'} });
                    const t = await r.text();
                    window.chrome.webview.postMessage(JSON.stringify({ ok: r.ok, status: r.status, body: t }));
                } catch (e) {
                    window.chrome.webview.postMessage(JSON.stringify({ ok:false, status:0, body:String(e) }));
                }
            })();";

            for (int attempt = 0; attempt < 8; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var raw = await RunFetchAsync(core, fetchScript, TimeSpan.FromSeconds(15), ct);
                if (raw != null)
                {
                    try
                    {
                        using var msg = JsonDocument.Parse(raw);
                        var el = msg.RootElement;
                        bool ok = el.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                        var body = el.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
                        if (ok && body.TrimStart().StartsWith("{"))
                        {
                            progress?.Report("Reading deck…");
                            return body;   // success — the deck JSON
                        }
                    }
                    catch (JsonException) { /* not our message shape; fall through to retry */ }
                }
                await Task.Delay(1500, ct);   // let Cloudflare's challenge auto-clear, then retry
            }

            throw new MoxfieldException(
                "Moxfield didn't return the deck (it may be private, or Cloudflare blocked the request). "
                + "You can use ⋯ More → Export on the deck and paste the list instead.");
        }
        catch (OperationCanceledException)
        {
            throw new MoxfieldException("Moxfield import was canceled.");
        }
        finally
        {
            try { web.Dispose(); } catch { }
            try { host.Close(); } catch { }
        }
    }

    private static async Task NavigateAsync(CoreWebView2 core, string url, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += Handler;
        try
        {
            core.Navigate(url);
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30), ct));
                if (done != tcs.Task) return;   // let the fetch-retry loop handle a slow/interstitial load
                await tcs.Task;
            }
        }
        finally { core.NavigationCompleted -= Handler; }
    }

    /// <summary>Runs the in-page fetch and waits for its posted result (or null on timeout).</summary>
    private static async Task<string?> RunFetchAsync(
        CoreWebView2 core, string script, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try { tcs.TrySetResult(e.TryGetWebMessageAsString()); }
            catch { tcs.TrySetResult(""); }
        }
        core.WebMessageReceived += Handler;
        try
        {
            await core.ExecuteScriptAsync(script);
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
                return done == tcs.Task ? await tcs.Task : null;
            }
        }
        finally { core.WebMessageReceived -= Handler; }
    }
}
