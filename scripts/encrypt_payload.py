#!/usr/bin/env python3
"""
encrypt_payload.py — Generate an encrypted verifysphere:// deep-link.

Identical to the Android version. Works for both Android and Windows builds
since both use the same AES-256-CBC key and wire format.

Usage:
    pip install pycryptodome
    python3 encrypt_payload.py \\
        --url "https://my-app.com" \\
        --sitekey "1x00000000000000000000AA" \\
        --callback "https://my-app.com/callback?session=abc123"

Output:
    verifysphere://verify?data=<encrypted_base64>

The same AES-256-CBC key compiled into the Android app AND the Windows EXE
is used here. Update KEY_BYTES below if you change the key in either
CryptoHelper.kt or CryptoHelper.cs.
"""

import argparse
import base64
import json
import os
import sys

try:
    from Crypto.Cipher import AES
    from Crypto.Util.Padding import pad
except ImportError:
    print("ERROR: pycryptodome not installed.")
    print("       Run: pip install pycryptodome")
    sys.exit(1)

# ----------------------------------------------------------------
# Must match CryptoHelper.kt (Android) AND CryptoHelper.cs (Windows)
# p1() + p2() + p3() + p4()
# ----------------------------------------------------------------
KEY_BYTES = bytes([
    0x56, 0x65, 0x72, 0x69, 0x66, 0x79, 0x53, 0x70,  # p1
    0x68, 0x65, 0x72, 0x65, 0x4B, 0x65, 0x79, 0x31,  # p2
    0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x42, 0x69,  # p3
    0x74, 0x53, 0x65, 0x63, 0x72, 0x65, 0x74, 0x21,  # p4
])


def encrypt(plaintext: str) -> str:
    iv = os.urandom(16)
    cipher = AES.new(KEY_BYTES, AES.MODE_CBC, iv)
    ct = cipher.encrypt(pad(plaintext.encode('utf-8'), AES.block_size))
    return base64.b64encode(iv + ct).decode('ascii')


def main():
    parser = argparse.ArgumentParser(
        description="Generate a verifysphere:// deep-link (works for Android AND Windows)"
    )
    parser.add_argument("--url",      required=True, help="Cloudflare-registered domain, e.g. https://my-app.com")
    parser.add_argument("--sitekey",  required=True, help="Cloudflare Turnstile sitekey")
    parser.add_argument("--callback", required=True, help="Callback URL (existing query params are preserved)")
    args = parser.parse_args()

    payload = {
        "url":         args.url,
        "sitekey":     args.sitekey,
        "callbackUrl": args.callback,
    }

    payload_json   = json.dumps(payload, separators=(',', ':'))
    encrypted      = encrypt(payload_json)
    from urllib.parse import quote
    safe_encrypted = quote(encrypted, safe='')

    deep_link = f"verifysphere://verify?data={safe_encrypted}"

    print()
    print("=== Generated Deep-Link (Android & Windows) ===")
    print()
    print(deep_link)
    print()
    print("=== Test on Android device ===")
    print()
    print(f"  adb shell am start -a android.intent.action.VIEW -d '{deep_link}'")
    print()
    print("=== Test on Windows (PowerShell, after URI scheme installed) ===")
    print()
    print(f"  Start-Process '{deep_link}'")
    print()
    print("=== Test sitekey reminder ===")
    print("  Always-pass: 1x00000000000000000000AA  (url: https://www.cloudflare.com)")
    print("  Always-fail: 2x00000000000000000000AB")
    print("  Interactive: 3x00000000000000000000FF")
    print()


if __name__ == "__main__":
    main()
