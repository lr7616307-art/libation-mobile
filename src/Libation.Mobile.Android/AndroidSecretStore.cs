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
        cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
        var ciphertext = cipher.DoFinal(Encoding.UTF8.GetBytes(value));
        var iv = cipher.GetIV();
        var payload = new byte[1 + iv.Length + ciphertext.Length];
        payload[0] = checked((byte)iv.Length);
        Buffer.BlockCopy(iv, 0, payload, 1, iv.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, 1 + iv.Length, ciphertext.Length);

        var encoded = Base64.EncodeToString(payload, Base64Flags.NoWrap);
        if (!_preferences.Edit()!.PutString(key, encoded)!.Commit())
            throw new IOException("Unable to persist encrypted secret.");

        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        var encoded = _preferences.GetString(key, null);
        if (encoded is null) return Task.FromResult<string?>(null);

        var payload = Base64.Decode(encoded, Base64Flags.NoWrap);
        if (payload.Length < 1)
            throw new CryptographicException("Stored secret payload is invalid.");

        var ivLength = payload[0];
        if (ivLength == 0 || payload.Length <= 1 + ivLength)
            throw new CryptographicException("Stored secret payload is invalid.");

        var iv = new byte[ivLength];
        var ciphertext = new byte[payload.Length - 1 - ivLength];
        Buffer.BlockCopy(payload, 1, iv, 0, ivLength);
        Buffer.BlockCopy(payload, 1 + ivLength, ciphertext, 0, ciphertext.Length);

        using var cipher = Cipher.GetInstance(Transformation);
        cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(128, iv));
        var plaintext = cipher.DoFinal(ciphertext);
        return Task.FromResult<string?>(Encoding.UTF8.GetString(plaintext));
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
