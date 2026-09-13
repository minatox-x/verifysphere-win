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
/// Mirrors the behaviour of TurnstileSDK.call() + TurnstileCallback on Android:
///   - Shows an embedded WebView with the Turnstile widget HTML
///   - Listens for the JavaScript callback (token or error)
///   - Exposes OnSuccess / OnFailure events exactly like TurnstileCallback
///
/// Key fix vs the original: the original used NavigateToString(), which gives
/// the page a null/opaque origin. Cloudflare Turnstile validates the page origin
/// against the sitekey's registered domain, so a null origin causes the widget
/// to immediately fire its error-callback and close - exactly the "loads then
/// shuts" symptom reported with real (non-test) sitekeys.
///
/// The fix mirrors what Android's WebView does with loadDataWithBaseURL():
///   1. Register a WebResourceRequested filter for the real payload URL
///   2. Navigate to that URL - so WebView2 treats it as the page origin
///   3. Intercept the request before any network call leaves the machine
///   4. Respond with our own Turnstile HTML, served with the real origin
/// Cloudflare sees the correct origin (matching the sitekey domain) in the
/// Referer / Origin headers; no actual network request to the payload URL
/// is ever made; the URL is never displayed anywhere in the UI.
/// </summary>
public sealed partial class TurnstileWindow : Window
{
    // -----------------------------------------------------------------------
    // Events - mirror TurnstileCallback interface
    // -----------------------------------------------------------------------
    public event Action<string>? OnSuccess;  // token string
    public event Action<string>? OnFailure;  // error code string

    private readonly string _url;
    private readonly string _sitekey;
    private bool _callbackFired;

    // Timeout mirror: "load_timeout" from Android SDK
    private System.Windows.Threading.DispatcherTimer? _loadTimer;
    private const int LoadTimeoutSeconds = 30;

    // The filter token returned by AddWebResourceRequestedFilter -
    // kept so we can remove it after first use to avoid handling
    // subsequent Cloudflare sub-resource requests.
    private bool _mainPageServed;

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

            var core = TurnstileWebView.CoreWebView2;

            // ------------------------------------------------------------------
            // Origin fix: intercept the navigation to _url and serve our own
            // Turnstile HTML instead of fetching the real page.
            //
            // This is the Windows equivalent of Android's:
            //   webView.loadDataWithBaseURL(url, html, "text/html", "utf-8", null)
            //
            // WebView2 will set the page origin to _url's origin (e.g.
            // https://my-app.com) for all purposes - including the Origin and
            // Referer headers sent to challenges.cloudflare.com when the
            // Turnstile script loads. Cloudflare sees the correct domain and
            // validates the sitekey. The actual payload URL is never fetched;
            // we cancel and replace the response before any outbound request.
            // ------------------------------------------------------------------
            core.AddWebResourceRequestedFilter(_url, CoreWebView2WebResourceContext.Document);
            core.WebResourceRequested += OnWebResourceRequested;

            // Wire remaining event handlers
            core.NavigationStarting  += OnNavigationStarting;
            core.WebMessageReceived  += OnWebMessageReceived;
            core.NavigationCompleted += OnNavigationCompleted;

            // Navigate to the real URL - WebView2 fires WebResourceRequested
            // before sending any network request, so we intercept it first.
            core.Navigate(_url);
        }
        catch (Exception)
        {
            FireFailure("load_timeout");
        }
    }

    // -----------------------------------------------------------------------
    // WebResource intercept - serve Turnstile HTML with the real URL as origin
    // -----------------------------------------------------------------------

    private void OnWebResourceRequested(object? sender,
        CoreWebView2WebResourceRequestedEventArgs e)
    {
        // Only intercept the first (main-page) request. Sub-resources from
        // Cloudflare (the api.js, challenge iframes, etc.) must go out normally.
        if (_mainPageServed) return;
        _mainPageServed = true;

        // Remove the filter so Cloudflare sub-resources are not intercepted.
        TurnstileWebView.CoreWebView2.RemoveWebResourceRequestedFilter(
            _url, CoreWebView2WebResourceContext.Document);

        var html    = BuildTurnstileHtml();
        var bytes   = Encoding.UTF8.GetBytes(html);
        var stream  = new System.IO.MemoryStream(bytes);

        // Respond with our HTML - same origin as _url, no actual network fetch.
        e.Response = TurnstileWebView.CoreWebView2.Environment.CreateWebResourceResponse(
            stream,
            statusCode:  200,
            reasonPhrase: "OK",
            headers: "Content-Type: text/html; charset=utf-8");
    }

    // -----------------------------------------------------------------------
    // Turnstile HTML
    // -----------------------------------------------------------------------

    private string BuildTurnstileHtml()
    {
        // Sitekey is injected server-side (before any JS runs) and HTML-encoded.
        // The payload URL is used only as the navigation target above - it is
        // never written into the HTML, never shown in the address bar (the
        // WebView has no chrome), and never accessible to page JavaScript.
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
    // WebView2 event handlers
    // -----------------------------------------------------------------------

    private void OnNavigationCompleted(object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        Dispatcher.Invoke(() => LoadingOverlay.Visibility = Visibility.Collapsed);
    }

    private void OnNavigationStarting(object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        // After our page is served, allow:
        //   - The initial navigation to _url (handled by WebResourceRequested above)
        //   - All cloudflare.com sub-navigations (challenge iframes, etc.)
        //   - about: / data: internal URIs
        // Block everything else.
        var uri = e.Uri ?? "";
        bool isAllowed =
            uri.Equals(_url, StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("about:",         StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("data:",          StringComparison.OrdinalIgnoreCase) ||
            uri.Contains("cloudflare.com",   StringComparison.OrdinalIgnoreCase);

        if (!isAllowed)
            e.Cancel = true;
    }

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
    // Timeout - mirrors Android SDK's "load_timeout" failure
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
    // Cancel button
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
