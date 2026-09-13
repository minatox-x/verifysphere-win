using System.Windows;

namespace VerifySphere;

/// <summary>
/// Port of Android's showFallbackDialog AlertDialog.
/// Shows the callback URL and a "Copy URL" button.
/// Mirrors: setTitle / setMessage / setPositiveButton("Copy URL") / setNegativeButton("Close")
/// </summary>
public sealed partial class FallbackDialog : Window
{
    private readonly string _callbackUrl;

    public FallbackDialog(string callbackUrl)
    {
        _callbackUrl = callbackUrl;
        InitializeComponent();
        Loaded += (_, _) => TbUrl.Text = callbackUrl;
    }

    // Mirrors .setPositiveButton("Copy URL") — copyToClipboard + Toast
    private void BtnCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_callbackUrl);

        // Brief visual feedback (mirrors Toast.makeText "URL copied to clipboard")
        BtnCopyUrl.Content = "Copied!";
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = System.TimeSpan.FromSeconds(1.5)
        };
        timer.Tick += (_, _) =>
        {
            BtnCopyUrl.Content = "Copy URL";
            timer.Stop();
        };
        timer.Start();
    }

    // Mirrors .setNegativeButton("Close", null)
    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
