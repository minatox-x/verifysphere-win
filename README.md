# VerifySphere (Windows)

> Windows port of the VerifySphere Android app. Handles `verifysphere://` deep-links, runs a Cloudflare Turnstile verification challenge in an embedded WebView2, and delivers the encrypted token back to a callback URL in the default browser.

**Exact same logic as the Android version.** Same AES-256-CBC crypto, same payload format, same flow, same UI states, same error codes. Encrypted payloads generated with `scripts/encrypt_payload.py` work for **both** the Android APK and the Windows EXE.

---

## How it works

```
Your app / web page
    │
    ▼  opens
verifysphere://verify?data=<AES-256 encrypted payload>
    │
    ▼  VerifySphere decrypts → extracts url, sitekey, callbackUrl
    │
    ▼  shows "Please wait…" screen + Turnstile WebView2 dialog
    │
    ▼  on success → encrypts token → appends to callbackUrl
    │
    ▼  opens callbackUrl?token=<encrypted_base64> in default browser
```

Everything sensitive (the site key, the callback URL, the raw token) is **never shown on screen**.

---

## Payload format

Identical to the Android version.

The `data=` parameter is **Base64( AES-256-CBC( JSON ) )** where the wire format is `IV[16 bytes] || CipherText`.

```json
{
  "url":         "https://my-app.com",
  "sitekey":     "0x4AAAAAAA...",
  "callbackUrl": "https://my-app.com/callback?session=abc123"
}
```

| Field | Description |
|---|---|
| `url` | The domain your Cloudflare sitekey is registered for. Must start with `https://`. |
| `sitekey` | Your Cloudflare Turnstile public sitekey. |
| `callbackUrl` | Where to send the token. Existing query params are preserved; `&token=` is appended. |

---

## Token delivery

After the challenge passes, the app:

1. Encrypts the raw Turnstile token with the same AES-256 key (new random IV each time).
2. URL-encodes the result.
3. Opens `<callbackUrl>?token=<encrypted_base64>` (or `&token=`) via the default browser.
4. Shows a fallback dialog with a **Copy URL** button in case no browser opens.

On your server, decrypt the `token` parameter with AES-256-CBC to get the raw Turnstile token, then verify it against `https://challenges.cloudflare.com/turnstile/v0/siteverify`.

---

## Encryption key

The default key in `CryptoHelper.cs` is:

```
VerifySpherKey1AES256BitSecret!
```

(32 bytes → AES-256)

**Replace this with your own random 32-byte key** before building. Edit the four `P1()–P4()` byte arrays in `CryptoHelper.cs` and update `KEY_BYTES` in `scripts/encrypt_payload.py` to match. If you're also using the Android app, update `CryptoHelper.kt` to the same key.

```bash
python3 -c "import os; k=os.urandom(32); print(list(k))"
```

---

## Requirements

- **Windows 10 / 11 (x64)**
- **Microsoft Edge WebView2 Runtime** — pre-installed on Windows 10 1803+ and all Windows 11. If missing, install from: https://developer.microsoft.com/microsoft-edge/webview2/
- **.NET 8 Runtime** — bundled in the self-contained EXE (no separate install needed)

---

## Installation

### 1. Download the release

From GitHub Releases, download:
- `VerifySphere-v*.exe`
- `install_uri_scheme.ps1`

Place both in the same folder, e.g. `C:\Program Files\VerifySphere\`.

### 2. Register the verifysphere:// URI scheme (once)

Open **PowerShell as Administrator** and run:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\install_uri_scheme.ps1
```

This writes the necessary Windows Registry keys so that clicking a `verifysphere://` link launches VerifySphere — the Windows equivalent of Android's `<intent-filter>` in `AndroidManifest.xml`.

To specify a custom EXE path:

```powershell
.\install_uri_scheme.ps1 -ExePath "C:\Program Files\VerifySphere\VerifySphere-v1.0.0.exe"
```

### 3. Test

```powershell
# Generate a test link
pip install pycryptodome
python3 scripts/encrypt_payload.py `
    --url "https://www.cloudflare.com" `
    --sitekey "1x00000000000000000000AA" `
    --callback "https://httpbin.org/get"

# Open the generated link (PowerShell)
Start-Process "verifysphere://verify?data=<output>"

# Or just paste it into a browser address bar
```

### 4. Uninstall

```powershell
.\install_uri_scheme.ps1 -Uninstall
```

---

## Building from source

### Prerequisites

- .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
- Windows 10 or 11 (x64)

### Build

```powershell
dotnet publish src/VerifySphere/VerifySphere.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o publish/
```

Output: `publish/VerifySphere.exe` (~110 MB self-contained)

---

## GitHub Actions CI/CD

Same structure as the Android workflow:

| Step | Android | Windows |
|---|---|---|
| Runner | `ubuntu-latest` | `windows-latest` |
| SDK setup | `actions/setup-java@v4` (JDK 17) | `actions/setup-dotnet@v4` (.NET 8) |
| Cache | Gradle | NuGet |
| Build command | `./gradlew :app:assembleRelease` | `dotnet publish` |
| Signing | JKS keystore via `-P` flags | N/A (no mandatory signing on Windows) |
| Output | `VerifySphere-v*.apk` | `VerifySphere-v*.exe` |
| Trigger | `v*` tag or manual | `v*` tag or manual |
| Release | GitHub Release with APK | GitHub Release with EXE + install script |

### Push a release tag

```bash
git add .
git commit -m "Initial Windows release"
git tag v1.0.0
git push origin main --tags
```

GitHub Actions will:
1. Build the self-contained release EXE.
2. Upload it as a workflow artifact (30-day retention).
3. Create a GitHub Release with the EXE and install script attached (on version tags).

You can also trigger manually: **Actions → Build & Release Windows EXE → Run workflow**.

---

## Generating a deep-link (server side)

```bash
pip install pycryptodome
python3 scripts/encrypt_payload.py \
    --url      "https://my-app.com" \
    --sitekey  "0x4AAAAAAA..." \
    --callback "https://my-app.com/verify/callback?session=abc123&user=42"
```

The generated `verifysphere://verify?data=<base64>` link works for **both** Android and Windows.

---

## Test sitekeys (Cloudflare)

| Sitekey | Behaviour |
|---|---|
| `1x00000000000000000000AA` | Always passes — no interaction needed |
| `2x00000000000000000000AB` | Always fails |
| `3x00000000000000000000FF` | Shows an interactive checkbox challenge |

Use `https://www.cloudflare.com` as the `url` for all test keys.

---

## Project structure

```
verifysphere-windows/
├── .github/workflows/
│   └── release.yml                  ← GitHub Actions CI/CD (Windows)
├── src/VerifySphere/
│   ├── App.xaml / App.xaml.cs       ← Entry point + URI arg parsing (≈ Application class)
│   ├── MainWindow.xaml              ← UI layout (≈ activity_main.xml)
│   ├── MainWindow.xaml.cs           ← Flow logic (≈ MainActivity.kt)
│   ├── TurnstileWindow.xaml         ← Turnstile WebView2 dialog (≈ TurnstileSDK DialogFragment)
│   ├── TurnstileWindow.xaml.cs      ← Turnstile logic (≈ TurnstileCallback)
│   ├── FallbackDialog.xaml          ← "Copy URL" dialog (≈ showFallbackDialog AlertDialog)
│   ├── FallbackDialog.xaml.cs
│   ├── CryptoHelper.cs              ← AES-256-CBC (≈ CryptoHelper.kt)
│   ├── IntentPayload.cs             ← Payload model (≈ IntentPayload.kt)
│   ├── PayloadParser.cs             ← JSON → IntentPayload (≈ PayloadParser.kt)
│   └── VerifySphere.csproj
├── scripts/
│   ├── install_uri_scheme.ps1       ← Register verifysphere:// in Registry (≈ AndroidManifest intent-filter)
│   └── encrypt_payload.py           ← Generate deep-links (identical to Android version)
└── README.md
```

---

## Security notes

Mirrors the Android security model:

- **Key obfuscation**: The AES key is split across four private methods in `CryptoHelper.cs` — same approach as `p1()–p4()` in the Android version. .NET's Release build + PublishSingleFile makes the key non-trivially hard to find but the same caveat applies: a determined attacker with a decompiler could recover it.
- **No disk writes**: Tokens are held in memory only and cleared when the window closes.
- **HTTPS only**: WebView2 settings block navigation to non-Cloudflare origins.
- **No UI leakage**: The decrypted URL, sitekey, callback URL, and raw token are never displayed on screen.
- **No persistent WebView data**: WebView2 user data is stored in a temp folder and holds no sensitive state.

---

## License

MIT
