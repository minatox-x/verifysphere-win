using System;
using System.Text.Json;

namespace VerifySphere;

/// <summary>
/// Exact port of Android PayloadParser.kt.
/// Takes the raw decrypted JSON string and returns an IntentPayload.
/// Throws ArgumentException if any required field is missing or invalid.
/// </summary>
internal static class PayloadParser
{
    public static IntentPayload Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var url         = root.TryGetProperty("url",         out var u) ? u.GetString()?.Trim() ?? "" : "";
        var sitekey     = root.TryGetProperty("sitekey",     out var s) ? s.GetString()?.Trim() ?? "" : "";
        var callbackUrl = root.TryGetProperty("callbackUrl", out var c) ? c.GetString()?.Trim() ?? "" : "";

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Missing or invalid 'url' in payload");

        if (string.IsNullOrWhiteSpace(sitekey))
            throw new ArgumentException("Missing 'sitekey' in payload");

        if (!callbackUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !callbackUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Missing or invalid 'callbackUrl' in payload");

        return new IntentPayload(Url: url, Sitekey: sitekey, CallbackUrl: callbackUrl);
    }
}
