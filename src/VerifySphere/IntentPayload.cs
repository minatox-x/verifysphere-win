namespace VerifySphere;

/// <summary>
/// Holds the decrypted payload fields extracted from the verifysphere:// deep-link.
/// Exact port of Android IntentPayload.kt.
///
/// Expected JSON after decryption:
/// {
///   "url":         "https://my-app.com",
///   "sitekey":     "0x4AAAAAAA...",
///   "callbackUrl": "https://my-app.com/callback?foo=bar"
/// }
/// </summary>
internal sealed record IntentPayload(
    string Url,
    string Sitekey,
    string CallbackUrl
);
