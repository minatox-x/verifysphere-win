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
/// ORIGIN FIX (the actual root cause of "flashes then fails"):
///
/// A page's origin is determined ONLY by the URL passed to Navigate().
/// It is NOT affected by response headers, and it is NOT something you can
/// fake by injecting "Origin:"/"Referer:" headers on an intercepted response -
/// browsers (and WebView2) compute origin from the navigated URL itself,
/// before any response is even received. An earlier attempt navigated to a
/// fake internal URL (https://verifysphere.internal/...) to hide the real
/// URL from the WebView - but that made the page's real origin
/// "https://verifysphere.internal", which does not match the domain the
/// sitekey is registered for. Cloudflare Turnstile checks this origin against
/// the sitekey's configured domain and fails immediately when they don't
/// match - exactly the instant "verification failed" symptom. The
/// always-pass test sitekey (1x0000...) skips this origin check entirely,
/// which is why it kept working while real sitekeys did not.
///
/// THE FIX: navigate to the REAL _url (so the browsing context's origin is
/// genuinely correct and matches what the sitekey expects), but intercept
/// that exact navigation request via WebResourceRequested and serve our own
/// HTML instead of ever actually fetching the real page. Interception does
/// NOT change the document's origin - only the URL passed to Navigate() does.
/// So Cloudflare sees the correct origin, no real network request to _url is
/// ever made, and the URL itself never touches any visible UI because the
/// WebView chrome (status bar, context menu, dev tools) is disabled below.
/// </summary>
public sealed partial class TurnstileWindow : Window
{
    public event Action<string>? OnSuccess;
    public event Action<string>? OnFailure;

    private readonly string _url;
    private readonly string _sitekey;
    private bool _callbackFired;
    private bool _mainPageServed;

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
            // Lock down the WebView chrome so the real URL is never visible,
            // even though we now navigate to it for correct origin behaviour.
            // ------------------------------------------------------------------
            settings.IsStatusBarEnabled             = false;  // no URL on hover
            settings.AreDefaultContextMenusEnabled  = false;  // no right-click menu
            settings.AreDevToolsEnabled             = false;  // no F12 / inspect
            settings.IsZoomControlEnabled           = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.IsBuiltInErrorPageEnabled      = false;
            settings.IsSwipeNavigationEnabled        = false;

            // ------------------------------------------------------------------
            // Intercept the exact navigation to _url. This is what lets us
            // navigate to the real URL (for correct origin) while never
            // actually sending a request over the network.
            // ------------------------------------------------------------------
            core.AddWebResourceRequestedFilter(_url, CoreWebView2WebResourceContext.Document);
            core.WebResourceRequested += OnWebResourceRequested;

            core.NavigationStarting  += OnNavigationStarting;
            core.WebMessageReceived  += OnWebMessageReceived;
            core.NavigationCompleted += OnNavigationCompleted;

            // Navigate to the REAL url - this is what gives Cloudflare the
            // correct origin. The request itself never leaves the machine
            // because WebResourceRequested intercepts it below.
            core.Navigate(_url);
        }
        catch (Exception)
        {
            FireFailure("load_timeout");
        }
    }

    // -----------------------------------------------------------------------
    // WebResource intercept - serve our HTML instead of fetching the real page
    // -----------------------------------------------------------------------

    private void OnWebResourceRequested(object? sender,
        CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_mainPageServed) return;
        _mainPageServed = true;

        // Remove the filter now - only the main document request needs
        // interception. Cloudflare's own sub-requests (api.js, challenge
        // iframes hosted on challenges.cloudflare.com) must go out for real.
        TurnstileWebView.CoreWebView2.RemoveWebResourceRequestedFilter(
            _url, CoreWebView2WebResourceContext.Document);

        var html   = BuildTurnstileHtml();
        var bytes  = Encoding.UTF8.GetBytes(html);
        var stream = new System.IO.MemoryStream(bytes);

        e.Response = TurnstileWebView.CoreWebView2.Environment.CreateWebResourceResponse(
            stream,
            200,
            "OK",
            "Content-Type: text/html; charset=utf-8");
    }

    // -----------------------------------------------------------------------
    // Turnstile HTML
    // -----------------------------------------------------------------------

    private string BuildTurnstileHtml()
    {
        // Only the sitekey (not secret) is embedded. _url itself is never
        // written into the HTML or exposed to page JavaScript.
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
    // Navigation guard
    // -----------------------------------------------------------------------

    private void OnNavigationStarting(object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = e.Uri ?? "";
        bool isAllowed =
            uri.Equals(_url,                   StringComparison.OrdinalIgnoreCase) ||
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
