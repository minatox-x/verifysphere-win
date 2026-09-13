using System;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Threading;

namespace VerifySphere;

/// <summary>
/// Windows port of Android MainActivity.kt.
///
/// Mirrors the exact same flow:
///   1. HandleUri()    → decrypt payload → parse → showWaiting → startVerification
///   2. StartVerification() → open TurnstileWindow (= TurnstileSDK.call())
///   3. onSuccess → encrypt token → build callback URL → open browser
///   4. onFailure → showRetry() or showError()
///   5. Retry button → startVerification() (same as btnRetry.setOnClickListener)
///   6. FallbackDialog for the URL (same as showFallbackDialog on Android)
/// </summary>
public sealed partial class MainWindow : Window
{
    // Holds decoded payload for the current session only — never stored to disk.
    // Mirrors `private var payload: IntentPayload?` in MainActivity.kt
    private IntentPayload? _payload;

    // -----------------------------------------------------------------------
    // Lifecycle — mirrors onCreate()
    // -----------------------------------------------------------------------

    public MainWindow()
    {
        InitializeComponent();

        // Post URI handling to the next dispatch pass so the window is fully loaded —
        // same as window.decorView.post { handleIntent(intent) } in Android.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            HandleUri(App.LaunchUri);
        });
    }

    // -----------------------------------------------------------------------
    // URI handling — mirrors handleIntent(Intent)
    // -----------------------------------------------------------------------

    private void HandleUri(string? rawUri)
    {
        if (string.IsNullOrWhiteSpace(rawUri))
        {
            ShowError("No verification request received.\n\nOpen a verifysphere:// link to begin.");
            return;
        }

        Uri uri;
        try { uri = new Uri(rawUri); }
        catch
        {
            ShowError("Invalid verification link — no payload found.");
            return;
        }

        if (!string.Equals(uri.Scheme, "verifysphere", StringComparison.OrdinalIgnoreCase))
        {
            ShowError("No verification request received.\n\nOpen a verifysphere:// link to begin.");
            return;
        }

        // Accepted forms — mirrors Android:
        //   verifysphere://verify?data=<base64>
        //   verifysphere://<base64>
        var query     = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var encrypted = query["data"]
            ?? (string.IsNullOrWhiteSpace(uri.Host)    ? null : uri.Host)
            ?? (uri.AbsolutePath.TrimStart('/') is { Length: > 0 } p ? p : null);

        if (string.IsNullOrWhiteSpace(encrypted))
        {
            ShowError("Invalid verification link — no payload found.");
            return;
        }

        try
        {
            var json = CryptoHelper.Decrypt(encrypted);
            _payload = PayloadParser.Parse(json);
            ShowWaiting();
            StartVerification();
        }
        catch
        {
            ShowError("Invalid or corrupted verification link.");
        }
    }

    // -----------------------------------------------------------------------
    // Retry button — mirrors btnRetry.setOnClickListener { startVerification() }
    // -----------------------------------------------------------------------

    private void BtnRetry_Click(object sender, RoutedEventArgs e)
    {
        StartVerification();
    }

    // -----------------------------------------------------------------------
    // Turnstile flow — mirrors startVerification()
    // -----------------------------------------------------------------------

    private void StartVerification()
    {
        var p = _payload;
        if (p == null)
        {
            ShowError("No verification data. Please retry the original link.");
            return;
        }

        ShowWaiting();

        // Open Turnstile dialog — mirrors TurnstileSDK.call(activity, url, sitekey, callback)
        var turnstileWindow = new TurnstileWindow(p.Url, p.Sitekey)
        {
            Owner = this
        };

        // TurnstileCallback.onSuccess — mirrors exact same handler
        turnstileWindow.OnSuccess += rawToken =>
        {
            Dispatcher.Invoke(() => DeliverToken(rawToken));
        };

        // TurnstileCallback.onFailure — mirrors exact same handler with same error codes
        turnstileWindow.OnFailure += error =>
        {
            Dispatcher.Invoke(() =>
            {
                switch (error)
                {
                    case "cancelled":
                        ShowRetry("Verification was cancelled.");
                        break;
                    case "load_timeout":
                        ShowRetry("Network timeout. Please check your connection and try again.");
                        break;
                    case "token_expired":
                        ShowRetry("Verification expired. Please try again.");
                        break;
                    default:
                        ShowRetry("Verification failed. Please try again.");
                        break;
                }
            });
        };

        // Show the Turnstile window as a dialog (modal, like Android DialogFragment)
        turnstileWindow.ShowDialog();
    }

    // -----------------------------------------------------------------------
    // Token delivery — mirrors deliverToken()
    // -----------------------------------------------------------------------

    private void DeliverToken(string rawToken)
    {
        var p = _payload;
        if (p == null) return;

        string encryptedToken;
        try
        {
            encryptedToken = CryptoHelper.Encrypt(rawToken);
        }
        catch
        {
            ShowRetry("Failed to prepare token. Please try again.");
            return;
        }

        var urlSafeToken      = Uri.EscapeDataString(encryptedToken);   // mirrors Uri.encode()
        var callbackWithToken = BuildCallbackUrl(p.CallbackUrl, urlSafeToken);

        ShowSuccess();
        OpenInBrowser(callbackWithToken);
    }

    // Mirrors buildCallbackUrl() exactly
    private static string BuildCallbackUrl(string callbackUrl, string urlSafeToken)
    {
        return callbackUrl.Contains('?')
            ? $"{callbackUrl}&token={urlSafeToken}"
            : $"{callbackUrl}?token={urlSafeToken}";
    }

    // Mirrors openInBrowser() — opens default browser + shows fallback dialog
    private void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Browser failed to open — handled by fallback dialog below
        }

        // Always show fallback dialog — mirrors showFallbackDialog() on Android
        ShowFallbackDialog(url);
    }

    // Mirrors showFallbackDialog() with same title/message/Copy URL button
    private void ShowFallbackDialog(string callbackUrl)
    {
        var dialog = new FallbackDialog(callbackUrl)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    // -----------------------------------------------------------------------
    // UI state helpers — mirrors showWaiting / showSuccess / showRetry / showError
    // -----------------------------------------------------------------------

    private void ShowWaiting()
    {
        LayoutWaiting.Visibility = Visibility.Visible;
        LayoutSuccess.Visibility = Visibility.Collapsed;
        LayoutError.Visibility   = Visibility.Collapsed;
        LayoutRetry.Visibility   = Visibility.Collapsed;
    }

    private void ShowSuccess()
    {
        LayoutWaiting.Visibility = Visibility.Collapsed;
        LayoutSuccess.Visibility = Visibility.Visible;
        LayoutError.Visibility   = Visibility.Collapsed;
        LayoutRetry.Visibility   = Visibility.Collapsed;
    }

    private void ShowRetry(string message)
    {
        LayoutWaiting.Visibility  = Visibility.Collapsed;
        LayoutSuccess.Visibility  = Visibility.Collapsed;
        LayoutError.Visibility    = Visibility.Collapsed;
        LayoutRetry.Visibility    = Visibility.Visible;
        TvRetryMessage.Text       = message;
    }

    private void ShowError(string message)
    {
        LayoutWaiting.Visibility = Visibility.Collapsed;
        LayoutSuccess.Visibility = Visibility.Collapsed;
        LayoutRetry.Visibility   = Visibility.Collapsed;
        LayoutError.Visibility   = Visibility.Visible;
        TvErrorMessage.Text      = message;
    }

    // mirrors onDestroy() — clear payload from memory
    protected override void OnClosed(EventArgs e)
    {
        _payload = null;
        base.OnClosed(e);
    }
}
