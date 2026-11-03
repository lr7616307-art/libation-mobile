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
        cipher.Init(Javax.Crypto.CipherMode.EncryptMode, GetOrCreateKey());
        var iv = cipher.GetIV();
        if (iv is null || iv.Length != EncryptedSecretEnvelope.GcmIvLength)
            throw new CryptographicException("Android Keystore returned an invalid AES-GCM IV.");

        cipher.UpdateAAD(EncryptedSecretEnvelope.GetAssociatedData(key));
        var ciphertext = cipher.DoFinal(Encoding.UTF8.GetBytes(value));
        var payload = EncryptedSecretEnvelope.Encode(iv, ciphertext);

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

        EncryptedSecretEnvelopeData envelope;
        try
        {
            envelope = EncryptedSecretEnvelope.Decode(payload);
        }
        catch (InvalidDataException exception)
        {
            throw new CryptographicException("Stored secret payload is invalid.", exception);
        }

        using var cipher = Cipher.GetInstance(Transformation);
        cipher.Init(Javax.Crypto.CipherMode.DecryptMode, GetExistingKey(), new GCMParameterSpec(128, envelope.Iv));
        if (!envelope.IsLegacy)
            cipher.UpdateAAD(EncryptedSecretEnvelope.GetAssociatedData(key));

        byte[] plaintext;
        try
        {
            plaintext = cipher.DoFinal(envelope.CiphertextAndTag);
        }
        catch (Javax.Crypto.AEADBadTagException exception)
        {
            throw new CryptographicException("Stored secret authentication failed.", exception);
        }

        var value = Encoding.UTF8.GetString(plaintext);
        if (envelope.IsLegacy)
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

    private static ISecretKey GetExistingKey()
    {
        var keyStore = KeyStore.GetInstance("AndroidKeyStore");
        keyStore.Load(null);
        if (!keyStore.ContainsAlias(KeyAlias))
            throw new CryptographicException("The Android Keystore key for stored secrets is missing.");
        return (ISecretKey)(keyStore.GetKey(KeyAlias, null)
            ?? throw new CryptographicException("Android Keystore key is unavailable."));
    }

    private ISecretKey GetOrCreateKey()
    {
        var keyStore = KeyStore.GetInstance("AndroidKeyStore");
        keyStore.Load(null);

        if (keyStore.ContainsAlias(KeyAlias))
            return (ISecretKey)(keyStore.GetKey(KeyAlias, null)
                ?? throw new CryptographicException("Android Keystore key is unavailable."));

        // Never create a replacement key while encrypted values still exist.
        // Doing so would make every other value permanently undecryptable.
        if (_preferences.All?.Count > 0)
            throw new CryptographicException("The Android Keystore key is missing while encrypted secrets still exist.");

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
