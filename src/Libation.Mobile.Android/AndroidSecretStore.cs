using System.Security.Cryptography;
using System.Text;
using Android.Content;
using Android.Security.Keystore;
using Android.Util;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Libation.Mobile.Core;

namespace Libation.Mobile.Android;

/// <summary>
/// Encrypts small secrets with an AES-256-GCM key held by Android Keystore.
/// Ciphertext is stored in app-private SharedPreferences; the key is non-exportable.
/// </summary>
public sealed class AndroidSecretStore : ISecretStore
{
    private const string PreferencesName = "libation_mobile_secrets";
    private const string KeyAlias = "libation_mobile_secret_key_v1";
    private const string Transformation = "AES/GCM/NoPadding";
    private const byte CurrentEnvelopeVersion = 2;
    private const int GcmIvLength = 12;
    private readonly ISharedPreferences _preferences;

    public AndroidSecretStore(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _preferences = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Unable to open private secret storage.");
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Validate(key, value);
        cancellationToken.ThrowIfCancellationRequested();

        using var cipher = Cipher.GetInstance(Transformation);
        cipher.Init(Javax.Crypto.CipherMode.EncryptMode, GetOrCreateKey());
        var iv = cipher.GetIV();
        if (iv is null || iv.Length != GcmIvLength)
            throw new CryptographicException("Android Keystore returned an invalid AES-GCM IV.");

        cipher.UpdateAAD(Encoding.UTF8.GetBytes(key));
        var ciphertext = cipher.DoFinal(Encoding.UTF8.GetBytes(value));
        var payload = new byte[2 + iv.Length + ciphertext.Length];
        payload[0] = CurrentEnvelopeVersion;
        payload[1] = checked((byte)iv.Length);
        Buffer.BlockCopy(iv, 0, payload, 2, iv.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, 2 + iv.Length, ciphertext.Length);

        var encoded = Base64.EncodeToString(payload, Base64Flags.NoWrap);
        if (!_preferences.Edit()!.PutString(key, encoded)!.Commit())
            throw new IOException("Unable to persist encrypted secret.");

        return Task.CompletedTask;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        var encoded = _preferences.GetString(key, null);
        if (encoded is null) return null;

        byte[] payload;
        try
        {
            payload = Base64.Decode(encoded, Base64Flags.NoWrap);
        }
        catch (Java.Lang.IllegalArgumentException exception)
        {
            throw new CryptographicException("Stored secret payload is invalid.", exception);
        }

        if (payload.Length < 1)
            throw new CryptographicException("Stored secret payload is invalid.");

        var isLegacy = payload[0] == GcmIvLength;
        int headerLength;
        int ivLength;
        if (isLegacy)
        {
            // Version 1 stored [IV length][IV][ciphertext] without AAD.
            headerLength = 1;
            ivLength = payload[0];
        }
        else
        {
            if (payload[0] != CurrentEnvelopeVersion || payload.Length < 2)
                throw new CryptographicException("Stored secret envelope version is unsupported.");
            headerLength = 2;
            ivLength = payload[1];
            if (ivLength != GcmIvLength)
                throw new CryptographicException("Stored secret payload is invalid.");
        }

        if (payload.Length <= headerLength + ivLength + 16)
            throw new CryptographicException("Stored secret payload is invalid.");

        var iv = new byte[ivLength];
        var ciphertext = new byte[payload.Length - headerLength - ivLength];
        Buffer.BlockCopy(payload, headerLength, iv, 0, iv.Length);
        Buffer.BlockCopy(payload, headerLength + ivLength, ciphertext, 0, ciphertext.Length);

        using var cipher = Cipher.GetInstance(Transformation);
        cipher.Init(Javax.Crypto.CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(128, iv));
        if (!isLegacy)
            cipher.UpdateAAD(Encoding.UTF8.GetBytes(key));

        byte[] plaintext;
        try
        {
            plaintext = cipher.DoFinal(ciphertext);
        }
        catch (Javax.Crypto.AEADBadTagException exception)
        {
            throw new CryptographicException("Stored secret authentication failed.", exception);
        }

        var value = Encoding.UTF8.GetString(plaintext);
        if (isLegacy)
        {
            // Upgrade only after successful legacy authentication. If the write fails,
            // propagate the error rather than silently claiming migration succeeded.
            await SetAsync(key, value, cancellationToken).ConfigureAwait(false);
        }

        return value;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_preferences.Edit()!.Remove(key)!.Commit())
            throw new IOException("Unable to remove stored secret.");
        return Task.CompletedTask;
    }

    private static ISecretKey GetOrCreateKey()
    {
        var keyStore = KeyStore.GetInstance("AndroidKeyStore");
        keyStore.Load(null);

        if (keyStore.ContainsAlias(KeyAlias))
            return (ISecretKey)(keyStore.GetKey(KeyAlias, null)
                ?? throw new CryptographicException("Android Keystore key is unavailable."));

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore");
        var spec = new KeyGenParameterSpec.Builder(
                KeyAlias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetKeySize(256)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetRandomizedEncryptionRequired(true)
            .Build();

        generator.Init(spec);
        return generator.GenerateKey();
    }

    private static void Validate(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (Encoding.UTF8.GetByteCount(value) > 16 * 1024)
            throw new ArgumentOutOfRangeException(nameof(value), "Secrets must be 16 KiB or smaller.");
    }
}
