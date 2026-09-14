# VerifySphere (Windows)

> Windows port of the VerifySphere Android app. Handles `verifysphere://` deep-links, runs a Cloudflare Turnstile verification challenge in an embedded WebView2, and delivers the encrypted token back to a callback URL in the default browser.

**Exact same logic as the Android version.** Same AES-256-CBC crypto, same payload format, same flow, same UI states, same error codes. Encrypted payloads generated with `scripts/encrypt_payload.py` work for **both** the Android APK and the Windows app.

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

The AES-256 key is split across four private methods (`P1()`–`P4()`) in `CryptoHelper.cs`, mirroring the `p1()`–`p4()` obfuscation in the Android app.

**Before building for your own deployment, replace the key with your own random 32 bytes:**

1. Generate a fresh key:
   ```bash
   python3 -c "import os; k=os.urandom(32); print(list(k))"
   ```
2. Split the 32 bytes into four groups of 8 and update `P1()`–`P4()` in `CryptoHelper.cs` accordingly.
3. Update `KEY_BYTES` in `scripts/encrypt_payload.py` to the same 32 bytes.
4. If you're also using the Android app, update `CryptoHelper.kt` to match.

Never commit your production key to a public repository — treat it the same as any other shared secret.

---

## Requirements

- **Windows 10 / 11 (x64)**
- **Microsoft Edge WebView2 Runtime** — pre-installed on Windows 10 1803+ and all Windows 11. If missing, install from: https://developer.microsoft.com/microsoft-edge/webview2/
- **.NET 8 Runtime** — bundled in the self-contained build (no separate install needed)

---

## Installation

### 1. Download and run the installer

From GitHub Releases, download `verifysphere-setup.exe` and run it.

- No Administrator rights required
- Installs to your user profile (`%LocalAppData%\Programs\VerifySphere`)
- Adds a Start Menu shortcut and (optionally) a Desktop shortcut
- Registers the `verifysphere://` URI scheme automatically — the Windows equivalent of Android's `<intent-filter>` in `AndroidManifest.xml`
- Shows up in **Settings → Apps → Installed apps**, with a working uninstaller

That's it — no PowerShell, no manual Registry edits, no separate script to run.

### 2. Test

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

### 3. Uninstall

**Settings → Apps → Installed apps → VerifySphere → Uninstall**

This removes the app and automatically de-registers the `verifysphere://` URI scheme — no leftover Registry entries.

---

## Building from source

### Prerequisites

- .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
- Windows 10 or 11 (x64)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) — only needed if you want to build the installer yourself (`installer/setup.iss`)

### Build the app

```powershell
dotnet publish src/VerifySphere/VerifySphere.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o publish/
```

Output: `publish/VerifySphere.exe` (~110 MB self-contained)

### Build the installer

```powershell
Copy-Item "publish/VerifySphere.exe" "installer/VerifySphere.exe"
ISCC.exe /DMyAppVersion="1.0.0" "installer\setup.iss"
```

Output: `installer/verifysphere-setup.exe`

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
| Packaging | APK only | Inno Setup installer |
| Output | `VerifySphere-v*.apk` | `verifysphere-setup.exe` |
| Trigger | `v*` tag or manual | `v*` tag or manual |
| Release | GitHub Release with APK | GitHub Release with installer |

### Push a release tag

```bash
git add .
git commit -m "Initial Windows release"
git tag v1.0.0
git push origin main --tags
```

GitHub Actions will:
1. Build the self-contained release EXE.
2. Compile the Inno Setup installer (`verifysphere-setup.exe`).
3. Upload both as a workflow artifact (30-day retention).
4. Create a GitHub Release with the installer attached (on version tags).

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
│   └── release.yml                  ← GitHub Actions CI/CD (build + installer + release)
├── installer/
│   └── setup.iss                    ← Inno Setup script → produces verifysphere-setup.exe
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
│   └── encrypt_payload.py           ← Generate deep-links (identical to Android version)
└── README.md
```

---

## Security notes

Mirrors the Android security model:

- **Key obfuscation**: The AES key is split across four private methods in `CryptoHelper.cs` — same approach as `p1()–p4()` in the Android version. Release build + PublishSingleFile makes the key non-trivially hard to find, but the same caveat applies: a determined attacker with a decompiler could recover it. Use your own key, not the placeholder shipped in this repo.
- **No disk writes**: Tokens are held in memory only and cleared when the window closes.
- **HTTPS only**: WebView2 settings block navigation to non-Cloudflare origins.
- **No UI leakage**: The decrypted URL, sitekey, callback URL, and raw token are never displayed on screen. The Turnstile dialog also has its status bar, context menu, and dev tools disabled so the underlying page can't be inspected.
- **No persistent WebView data**: WebView2 user data is stored in a temp folder and holds no sensitive state.
- **No admin rights, clean uninstall**: The installer registers the `verifysphere://` URI scheme per-user (no elevation needed) and fully removes it on uninstall — no leftover Registry entries.

---

## License

MIT
