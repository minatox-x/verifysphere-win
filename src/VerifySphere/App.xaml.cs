using System;
using System.Windows;

namespace VerifySphere;

/// <summary>
/// Mirrors Android's intent dispatch:
///   - On first launch (no args)  → show error state (cold open)
///   - On URI arg launch          → parse verifysphere:// URI, hand to MainWindow
/// On Windows, custom URI scheme launches are passed as a command-line argument:
///   VerifySphere.exe "verifysphere://verify?data=<base64>"
/// The URI scheme registration is done by the installer / GitHub Actions setup script.
/// </summary>
public partial class App : Application
{
    // The URI received from the command-line argument (if any)
    internal static string? LaunchUri { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Windows passes the custom-URI as the first arg when the user clicks a link
        if (e.Args.Length > 0)
        {
            var arg = e.Args[0].Trim();
            if (arg.StartsWith("verifysphere://", StringComparison.OrdinalIgnoreCase))
            {
                LaunchUri = arg;
            }
        }

        var window = new MainWindow();
        window.Show();
    }
}
