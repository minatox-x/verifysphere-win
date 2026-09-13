using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace VerifySphere;

/// <summary>
/// Renders a Cloudflare Turnstile challenge inside a WebView2 dialog.
///
/// Mirrors the behaviour of TurnstileSDK.call() + TurnstileCallback on Android:
///   - Shows an embedded WebView with the Turnstile widget HTML
///   - Listens for the JavaScript callback (token or error)
///   - Exposes OnSuccess / OnFailure events exactly like TurnstileCallback
///
/// The HTML page injected mirrors what the Turnstile SDK does on Android:
/// loads challenges.cloudflare.com/turnstile/v0/api.js and renders the widget.
/// </summary>
public sealed partial class TurnstileWindow : Window
{
    // -----------------------------------------------------------------------
    // Events — mirror TurnstileCallback interface
    // -----------------------------------------------------------------------
    public event Action<string>? OnSuccess;  // token string
    public event Action<string>? OnFailure;  // error code string

    private readonly string _url;
    private readonly string _sitekey;
    private bool _callbackFired;

    // Timeout mirror: "load_timeout" from Android SDK
    private System.Windows.Threading.DispatcherTimer? _loadTimer;
    private const int LoadTimeoutSeconds = 30;

    public TurnstileWindow(string url, string sitekey)
    {
        _url     = url;
        _sitekey = sitekey;
        InitializeComponent();
        Loaded  += TurnstileWindow_Loaded;
        Closed  += TurnstileWindow_Closed;
    }

    // -----------------------------------------------------------------------
    // Init
    // -----------------------------------------------------------------------

    private async void TurnstileWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StartLoadTimeout();
        await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            // WebView2 needs a user data folder; use a temp path (no sensitive data persisted)
            var userDataFolder = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "VerifySphere_WebView2");

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);

            await TurnstileWebView.EnsureCoreWebView2Async(env);

            // Block all navigation away from the Turnstile page — security: mirrors
            // Android WebView's shouldOverrideUrlLoading() restriction
            TurnstileWebView.CoreWebView2.NavigationStarting  += OnNavigationStarting;
            TurnstileWebView.CoreWebView2.WebMessageReceived  += OnWebMessageReceived;
            TurnstileWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            // Inject the Turnstile HTML — same approach as TurnstileSDK WebView on Android
            TurnstileWebView.CoreWebView2.NavigateToString(BuildTurnstileHtml());
        }
        catch (Exception)
        {
            FireFailure("load_timeout");
        }
    }

    // -----------------------------------------------------------------------
    // Turnstile HTML — mirrors what Android TurnstileSDK loads in its WebView
    // -----------------------------------------------------------------------

    private string BuildTurnstileHtml()
    {
        // The page:
        //  1. Loads the Turnstile script from Cloudflare
        //  2. Renders the widget for the given sitekey
        //  3. On callback=token  → postMessage({ type:"success", token:"..." })
        //  4. On error / expired → postMessage({ type:"error",   error:"..." })
        //  5. On expired token   → postMessage({ type:"expired" })
        // This exactly mirrors the bridge the Android TurnstileSDK uses
        // (JavaScript → Java/Kotlin bridge via addJavascriptInterface).

        var escapedUrl     = System.Net.WebUtility.HtmlEncode(_url);
        var escapedSitekey = System.Net.WebUtility.HtmlEncode(_sitekey);

        return $@"<!DOCTYPE html>
<html>
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <title>Verification</title>
  <script src=""https://challenges.cloudflare.com/turnstile/v0/api.js"" async defer></script>
  <style>
    * {{ margin:0; padding:0; box-sizing:border-box; }}
    html, body {{
      width:100%; height:100%;
      background:#1E1E2E;
      display:flex; align-items:center; justify-content:center;
      font-family:system-ui,-apple-system,sans-serif;
    }}
    #container {{
      display:flex; flex-direction:column;
      align-items:center; justify-content:center;
      gap:16px; padding:24px;
    }}
    p {{
      color:#94A3B8; font-size:13px; text-align:center;
    }}
  </style>
</head>
<body>
  <div id=""container"">
    <p>Please complete the verification below.</p>
    <div id=""cf-widget""></div>
  </div>
  <script>
    // Wait for Turnstile API to be ready
    function renderWidget() {{
      if (typeof turnstile === 'undefined') {{
        setTimeout(renderWidget, 100);
        return;
      }}
      turnstile.render('#cf-widget', {{
        sitekey:  '{escapedSitekey}',
        // Mirrors the url field from the payload — Turnstile validates against the domain
        // The app host used as the 'base' URL context
        callback: function(token) {{
          window.chrome.webview.postMessage(JSON.stringify({{ type:'success', token:token }}));
        }},
        'error-callback': function(code) {{
          window.chrome.webview.postMessage(JSON.stringify({{ type:'error', error: code || 'unknown' }}));
        }},
        'expired-callback': function() {{
          window.chrome.webview.postMessage(JSON.stringify({{ type:'expired' }}));
        }},
        'timeout-callback': function() {{
          window.chrome.webview.postMessage(JSON.stringify({{ type:'error', error:'token_expired' }}));
        }},
        theme: 'dark',
        language: 'auto',
        appearance: 'always'
      }});
    }}
    document.addEventListener('DOMContentLoaded', renderWidget);
  </script>
</body>
</html>";
    }

    // -----------------------------------------------------------------------
    // WebView2 event handlers
    // -----------------------------------------------------------------------

    private void OnNavigationCompleted(object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        // Hide loading overlay once the page is ready
        Dispatcher.Invoke(() => LoadingOverlay.Visibility = Visibility.Collapsed);
    }

    private void OnNavigationStarting(object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        // Block any navigation that isn't the initial Turnstile page or Cloudflare resources.
        // Mirrors Android's WebViewClient.shouldOverrideUrlLoading() restriction.
        var uri = e.Uri ?? "";
        bool isAllowed =
            uri.StartsWith("about:",      StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("data:",       StringComparison.OrdinalIgnoreCase) ||
            uri.Contains("cloudflare.com", StringComparison.OrdinalIgnoreCase);

        if (!isAllowed)
        {
            e.Cancel = true;
        }
    }

    private void OnWebMessageReceived(object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            if (json == null) return;

            using var doc  = JsonDocument.Parse(json);
            var root       = doc.RootElement;
            var type       = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "success":
                {
                    var token = root.TryGetProperty("token", out var tk)
                        ? tk.GetString() ?? ""
                        : "";

                    if (string.IsNullOrWhiteSpace(token))
                    {
                        FireFailure("token_expired");
                    }
                    else
                    {
                        StopLoadTimeout();
                        FireSuccess(token);
                    }
                    break;
                }

                case "expired":
                    FireFailure("token_expired");
                    break;

                case "error":
                {
                    var code = root.TryGetProperty("error", out var err)
                        ? err.GetString() ?? "unknown"
                        : "unknown";
                    FireFailure(code);
                    break;
                }
            }
        }
        catch
        {
            FireFailure("unknown");
        }
    }

    // -----------------------------------------------------------------------
    // Timeout — mirrors Android SDK's "load_timeout" failure
    // -----------------------------------------------------------------------

    private void StartLoadTimeout()
    {
        _loadTimer          = new System.Windows.Threading.DispatcherTimer();
        _loadTimer.Interval = TimeSpan.FromSeconds(LoadTimeoutSeconds);
        _loadTimer.Tick    += (_, _) => FireFailure("load_timeout");
        _loadTimer.Start();
    }

    private void StopLoadTimeout()
    {
        _loadTimer?.Stop();
        _loadTimer = null;
    }

    // -----------------------------------------------------------------------
    // Cancel button — mirrors "cancelled" from Android SDK
    // -----------------------------------------------------------------------

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        FireFailure("cancelled");
    }

    private void TurnstileWindow_Closed(object? sender, EventArgs e)
    {
        // If the window is closed without a callback, treat as cancelled
        if (!_callbackFired)
            FireFailure("cancelled");
    }

    // -----------------------------------------------------------------------
    // Fire callbacks — mirrors TurnstileCallback.onSuccess / onFailure
    // -----------------------------------------------------------------------

    private void FireSuccess(string token)
    {
        if (_callbackFired) return;
        _callbackFired = true;
        StopLoadTimeout();
        Dispatcher.Invoke(() =>
        {
            OnSuccess?.Invoke(token);
            Close();
        });
    }

    private void FireFailure(string error)
    {
        if (_callbackFired) return;
        _callbackFired = true;
        StopLoadTimeout();
        Dispatcher.Invoke(() =>
        {
            OnFailure?.Invoke(error);
            Close();
        });
    }
}
