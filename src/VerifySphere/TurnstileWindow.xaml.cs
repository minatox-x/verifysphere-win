using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace VerifySphere;

/// <summary>
/// Renders a Cloudflare Turnstile challenge inside a WebView2 dialog.
///
/// Origin strategy (mirrors Android loadDataWithBaseURL):
///   Android sets baseUrl so the WebView reports the correct origin to
///   Cloudflare without ever fetching that URL.  We do the same on Windows:
///
///   1. AddWebResourceRequestedFilter intercepts ALL requests to a stable
///      fake internal URL (https://verifysphere.internal/turnstile).
///   2. We navigate to that fake URL - so the real payload URL (_url) never
///      touches the WebView navigation pipeline at all, never appears in any
///      event, and is never reachable by the user.
///   3. WebResourceRequested fires; we respond with our HTML but also inject
///      a "Referer: <_url>" and "Origin: <origin of _url>" header into the
///      response so that the Turnstile api.js sub-requests carry the correct
///      origin when they call challenges.cloudflare.com.
///   4. All WebView2 chrome features that could expose internals are disabled:
///      context menu, status bar, dev tools, default download UI.
///
/// Result: the user sees only the widget, no URL, no HTML, no right-click menu.
/// Cloudflare sees the correct domain origin.  The payload URL stays private.
/// </summary>
public sealed partial class TurnstileWindow : Window
{
    // -----------------------------------------------------------------------
    // Events - mirror TurnstileCallback interface
    // -----------------------------------------------------------------------
    public event Action<string>? OnSuccess;
    public event Action<string>? OnFailure;

    private readonly string _url;
    private readonly string _sitekey;
    private bool _callbackFired;
    private bool _mainPageServed;

    // Fake internal navigation URL - never leaves the process
    private const string InternalUrl = "https://verifysphere.internal/turnstile";

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
            var userDataFolder = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "VerifySphere_WebView2");

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);

            await TurnstileWebView.EnsureCoreWebView2Async(env);

            var core     = TurnstileWebView.CoreWebView2;
            var settings = core.Settings;

            // ------------------------------------------------------------------
            // Lock down the WebView so nothing internal is ever visible to user
            // ------------------------------------------------------------------
            settings.IsStatusBarEnabled             = false;  // no URL in bottom bar
            settings.AreDefaultContextMenusEnabled  = false;  // no right-click menu
            settings.AreDevToolsEnabled             = false;  // no F12 / inspect
            settings.IsZoomControlEnabled           = false;  // no Ctrl+scroll zoom UI
            settings.AreDefaultScriptDialogsEnabled = false;  // no alert/confirm popups
            settings.IsBuiltInErrorPageEnabled      = false;  // no WebView error pages
            settings.IsSwipeNavigationEnabled       = false;  // no swipe back/forward

            // ------------------------------------------------------------------
            // Intercept the fake internal URL we will navigate to.
            // The real payload URL (_url) is NEVER passed to core.Navigate()
            // so it never appears in any WebView event or UI surface.
            // ------------------------------------------------------------------
            core.AddWebResourceRequestedFilter(
                InternalUrl, CoreWebView2WebResourceContext.Document);
            core.WebResourceRequested += OnWebResourceRequested;

            // Wire other handlers
            core.NavigationStarting  += OnNavigationStarting;
            core.WebMessageReceived  += OnWebMessageReceived;
            core.NavigationCompleted += OnNavigationCompleted;

            // Navigate to the fake URL - real URL stays private
            core.Navigate(InternalUrl);
        }
        catch (Exception)
        {
            FireFailure("load_timeout");
        }
    }

    // -----------------------------------------------------------------------
    // WebResource intercept
    // -----------------------------------------------------------------------

    private void OnWebResourceRequested(object? sender,
        CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_mainPageServed) return;
        _mainPageServed = true;

        // Remove filter - only needed for the one main-page request
        TurnstileWebView.CoreWebView2.RemoveWebResourceRequestedFilter(
            InternalUrl, CoreWebView2WebResourceContext.Document);

        var html   = BuildTurnstileHtml();
        var bytes  = Encoding.UTF8.GetBytes(html);
        var stream = new System.IO.MemoryStream(bytes);

        // Build the origin of _url so Cloudflare sub-requests carry it
        // e.g. "https://my-app.com" from "https://my-app.com/somepage"
        string payloadOrigin;
        try
        {
            var parsed = new Uri(_url);
            payloadOrigin = $"{parsed.Scheme}://{parsed.Authority}";
        }
        catch
        {
            payloadOrigin = _url;
        }

        // Serve our HTML with correct Content-Type.
        // Also set Referer and Origin response headers so the Turnstile
        // api.js requests inherit the correct domain context.
        // These are HTTP response headers on the synthetic response -
        // they are never visible to the user, only to the Cloudflare
        // script running inside the WebView.
        var headers =
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Referer: {_url}\r\n" +
            $"Origin: {payloadOrigin}";

        e.Response = TurnstileWebView.CoreWebView2.Environment
            .CreateWebResourceResponse(stream, 200, "OK", headers);
    }

    // -----------------------------------------------------------------------
    // Turnstile HTML
    // -----------------------------------------------------------------------

    private string BuildTurnstileHtml()
    {
        // _url and payloadOrigin are NOT written into the HTML.
        // Only the sitekey (which is not secret) is embedded.
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
    function renderWidget() {{
      if (typeof turnstile === 'undefined') {{
        setTimeout(renderWidget, 100);
        return;
      }}
      turnstile.render('#cf-widget', {{
        sitekey: '{escapedSitekey}',
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
    // Navigation guard - only allow Cloudflare and internal resources
    // -----------------------------------------------------------------------

    private void OnNavigationStarting(object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = e.Uri ?? "";
        bool isAllowed =
            uri.Equals(InternalUrl,            StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("about:",           StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("data:",            StringComparison.OrdinalIgnoreCase) ||
            uri.Contains("cloudflare.com",     StringComparison.OrdinalIgnoreCase);

        if (!isAllowed)
            e.Cancel = true;
    }

    private void OnNavigationCompleted(object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        Dispatcher.Invoke(() => LoadingOverlay.Visibility = Visibility.Collapsed);
    }

    // -----------------------------------------------------------------------
    // Web message handling
    // -----------------------------------------------------------------------

    private void OnWebMessageReceived(object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            if (json == null) return;

            using var doc = JsonDocument.Parse(json);
            var root      = doc.RootElement;
            var type      = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "success":
                {
                    var token = root.TryGetProperty("token", out var tk)
                        ? tk.GetString() ?? ""
                        : "";
                    if (string.IsNullOrWhiteSpace(token))
                        FireFailure("token_expired");
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
    // Timeout
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
    // Cancel / close
    // -----------------------------------------------------------------------

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        FireFailure("cancelled");
    }

    private void TurnstileWindow_Closed(object? sender, EventArgs e)
    {
        if (!_callbackFired)
            FireFailure("cancelled");
    }

    // -----------------------------------------------------------------------
    // Fire callbacks
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
