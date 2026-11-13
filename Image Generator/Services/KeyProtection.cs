using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Image_Generator.Services;

/// <summary>
/// Machine-scoped encryption for sensitive strings (API keys) at rest.
///
/// Uses AES-256-GCM (authenticated encryption) with a key derived from the
/// machine+user identity via PBKDF2-SHA256, so the encrypted value stored
/// in the config file is useless on any other machine or for any other user.
/// The key is not stored alongside the config — it is derived at runtime.
///
/// Threat model: protects against casual config-file exposure (someone browsing
/// the user's files, copying preferences.json to another machine). It does NOT
/// protect against an attacker who can run code on the same machine — they
/// could derive the key the same way this class does, or read the key from
/// the running application's memory.
/// </summary>
public class KeyProtection
{
    private const string AppSalt = "DBM-Image-Generator-v1";
    private const int NonceLength = 12; // 96-bit nonce (standard for GCM)
    private const int TagLength = 16;   // 128-bit authentication tag
    private const int KeySize = 32;     // 256-bit key

    /// <summary>
    /// Derives a 256-bit AES key from machine+user identity + app scope.
    /// Deterministic for the same machine, user, and app — so previously
    /// encrypted values remain decryptable across restarts.
    ///
    /// The derivation uses:
    ///   - Machine name + user name + app-data path (stable identity)
    ///   - An app-specific salt (scopes the key to this application)
    ///   - PBKDF2-SHA256 (100k iterations, standard work factor)
    /// </summary>
    private static byte[] DeriveKey()
    {
        var machineContext = $"{Environment.MachineName}|{Environment.UserName}|" +
            $"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}";

        var password = Encoding.UTF8.GetBytes(machineContext);
        var salt = Encoding.UTF8.GetBytes(AppSalt);

        return Rfc2898DeriveBytes.Pbkdf2(
            password, salt, 100_000, HashAlgorithmName.SHA256, KeySize);
    }

    /// <summary>
    /// Encrypts a plaintext string using AES-256-GCM with a random nonce.
    ///
    /// Returns: base64(nonce(12) + tag(16) + ciphertext(N))
    /// Returns the input unchanged if null or empty.
    /// Falls back to returning plaintext if encryption is unavailable.
    /// </summary>
    public static string Encrypt(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText ?? string.Empty;

        try
        {
            var key = DeriveKey();
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var ciphertext = new byte[plainBytes.Length];
            var tag = new byte[TagLength];

            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plainBytes, ciphertext, tag);

            // Pack: nonce + tag + ciphertext
            var packed = new byte[
                NonceLength + TagLength + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, packed, 0, NonceLength);
            Buffer.BlockCopy(tag, 0, packed, NonceLength, TagLength);
            Buffer.BlockCopy(ciphertext, 0,
                packed, NonceLength + TagLength, ciphertext.Length);

            return Convert.ToBase64String(packed);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[KeyProtection] Encrypt failed: {ex.Message}");
            return plainText; // fallback: store unencrypted
        }
    }

    /// <summary>
    /// Decrypts a value previously produced by Encrypt().
    ///
    /// Handles legacy plaintext gracefully: if the input doesn't look like
    /// encrypted data (too short, invalid base64), it's returned as-is for
    /// backward compatibility with configs saved before encryption.
    ///
    /// Returns empty string when decryption fails (wrong machine or user,
    /// data corruption, key file missing). The user re-enters their key,
    /// which gets re-encrypted with the current machine's key.
    /// </summary>
    public static string Decrypt(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return cipherText ?? string.Empty;

        // Quick check: encrypted payload is at least base64(12+16+1) bytes,
        // which encodes to ~40 characters. Base64 length is always a multiple
        // of 4. Short values can't be our format — treat as legacy plaintext.
        if (cipherText.Length < 40 || cipherText.Length % 4 != 0)
            return cipherText;

        byte[] packed;
        try
        {
            packed = Convert.FromBase64String(cipherText);
        }
        catch (FormatException)
        {
            return cipherText; // legacy plaintext
        }

        if (packed.Length < NonceLength + TagLength)
            return cipherText;

        try
        {
            var key = DeriveKey();
            var nonce = packed[..NonceLength];
            var tag = packed[NonceLength..(NonceLength + TagLength)];
            var cipherBytes = packed[(NonceLength + TagLength)..];

            var plainBytes = new byte[cipherBytes.Length];

            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException)
        {
            // GCM authentication failed — wrong machine/user or tampered data
            System.Diagnostics.Debug.WriteLine(
                "[KeyProtection] Decrypt failed — key mismatch or corrupted data");
            return string.Empty;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[KeyProtection] Decrypt failed: {ex.Message}");
            return string.Empty;
        }
    }
}