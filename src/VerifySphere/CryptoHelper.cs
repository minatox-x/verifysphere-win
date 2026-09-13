using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VerifySphere;

/// <summary>
/// AES-256-CBC encryption helper.
///
/// Exact port of Android CryptoHelper.kt:
///   - Same key (split across 4 methods to resist simple string search)
///   - Same wire format: Base64( IV[16 bytes] || CipherText )
///   - Same padding: PKCS7 (equivalent to Java's PKCS5Padding for AES block size)
///
/// Uses only .NET built-in System.Security.Cryptography — no third-party deps.
/// </summary>
internal static class CryptoHelper
{
    // Key is split into 4 parts — mirrors p1()–p4() in CryptoHelper.kt.
    // Values are identical byte-for-byte to the Android implementation.
    private static byte[] P1() => new byte[] { 0x56, 0x65, 0x72, 0x69, 0x66, 0x79, 0x53, 0x70 };
    private static byte[] P2() => new byte[] { 0x68, 0x65, 0x72, 0x65, 0x4B, 0x65, 0x79, 0x31 };
    private static byte[] P3() => new byte[] { 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x42, 0x69 };
    private static byte[] P4() => new byte[] { 0x74, 0x53, 0x65, 0x63, 0x72, 0x65, 0x74, 0x21 };

    private static byte[] BuildKey()
    {
        // 32 bytes → AES-256; concatenation mirrors p1() + p2() + p3() + p4() in Kotlin
        var key = new byte[32];
        var p1 = P1(); var p2 = P2(); var p3 = P3(); var p4 = P4();
        Buffer.BlockCopy(p1, 0, key, 0,  8);
        Buffer.BlockCopy(p2, 0, key, 8,  8);
        Buffer.BlockCopy(p3, 0, key, 16, 8);
        Buffer.BlockCopy(p4, 0, key, 24, 8);
        return key;
    }

    /// <summary>
    /// Decrypt a Base64-encoded ciphertext (IV prepended).
    /// Returns the plaintext string, or throws on failure.
    /// Mirrors CryptoHelper.kt decrypt().
    /// </summary>
    public static string Decrypt(string base64Input)
    {
        var raw = Convert.FromBase64String(base64Input.Trim());

        if (raw.Length <= 16)
            throw new ArgumentException("Payload too short");

        var iv = raw[..16];
        var ct = raw[16..];

        using var aes = Aes.Create();
        aes.Key     = BuildKey();
        aes.IV      = iv;
        aes.Mode    = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7; // same as Java PKCS5Padding for AES

        using var decryptor = aes.CreateDecryptor();
        using var ms        = new MemoryStream(ct);
        using var cs        = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
        using var sr        = new StreamReader(cs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    /// <summary>
    /// Encrypt a plaintext string.
    /// Returns Base64( IV[16] || CipherText ).
    /// Mirrors CryptoHelper.kt encrypt().
    /// </summary>
    public static string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key     = BuildKey();
        aes.Mode    = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        var iv = aes.IV;
        using var encryptor = aes.CreateEncryptor();
        using var ms        = new MemoryStream();
        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            cs.Write(bytes, 0, bytes.Length);
        }

        var ct     = ms.ToArray();
        var result = new byte[iv.Length + ct.Length];
        Buffer.BlockCopy(iv, 0, result, 0,          iv.Length);
        Buffer.BlockCopy(ct, 0, result, iv.Length,  ct.Length);
        return Convert.ToBase64String(result);
    }
}
