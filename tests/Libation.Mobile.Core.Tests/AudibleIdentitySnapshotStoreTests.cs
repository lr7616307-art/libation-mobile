using System.Security.Cryptography;
using System.Text;
using Libation.Mobile.Audible;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleIdentitySnapshotStoreTests
{
    [Fact]
    public async Task EncryptedPayload_RoundTripsCompleteIdentityWithoutPlaintextAtRest()
    {
        var secrets = new AesGcmSecretStore();
        var store = new AudibleIdentitySnapshotStore(secrets);
        var snapshot = CreateSnapshot();

        await store.SaveAsync(snapshot);

        Assert.NotNull(secrets.StoredCiphertext);
        Assert.DoesNotContain(snapshot.AccessToken, secrets.StoredCiphertext);
        Assert.DoesNotContain(snapshot.RefreshToken, secrets.StoredCiphertext);
        Assert.DoesNotContain(snapshot.PrivateKey, secrets.StoredCiphertext);

        var restored = await store.LoadAsync();

        Assert.NotNull(restored);
        Assert.Equal(snapshot, restored);
        Assert.Equal(snapshot.Cookies, restored!.Cookies);
    }

    [Fact]
    public void UnsupportedSchemaVersion_IsRejected()
    {
        var json = AudibleIdentitySnapshotSerializer.Serialize(CreateSnapshot());
        var unsupported = json.Replace("\"schemaVersion\":1", "\"schemaVersion\":999", StringComparison.Ordinal);

        var error = Assert.Throws<InvalidDataException>(
            () => AudibleIdentitySnapshotSerializer.Deserialize(unsupported));

        Assert.Contains("version", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CorruptedEncryptedPayload_FailsClosedAsMissingIdentity()
    {
        var secrets = new AesGcmSecretStore();
        var store = new AudibleIdentitySnapshotStore(secrets);
        await store.SaveAsync(CreateSnapshot());
        secrets.CorruptStoredCiphertext();

        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task StorageFailure_PropagatesAndDoesNotPretendToSave()
    {
        var secrets = new AesGcmSecretStore { SetException = new IOException("simulated write failure") };
        var store = new AudibleIdentitySnapshotStore(secrets);

        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(CreateSnapshot()));

        Assert.Null(secrets.StoredCiphertext);
    }

    [Fact]
    public async Task UnsupportedSnapshot_IsNotWrittenToSecretStore()
    {
        var secrets = new AesGcmSecretStore();
        var store = new AudibleIdentitySnapshotStore(secrets);
        var unsupported = CreateSnapshot() with { SchemaVersion = 2 };

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(unsupported));

        Assert.Null(secrets.StoredCiphertext);
        Assert.Equal(0, secrets.SetCalls);
    }

    private static AudibleIdentitySnapshot CreateSnapshot() => new()
    {
        SchemaVersion = AudibleIdentitySnapshot.CurrentSchemaVersion,
        Provider = AudibleIdentitySnapshot.ProviderName,
        AudibleApiVersion = AudibleIdentitySnapshot.SupportedAudibleApiVersion,
        LocaleName = "us",
        AccessToken = "Atna|unit-test-access",
        AccessTokenExpiresAtUtc = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero),
        RefreshToken = "Atnr|unit-test-refresh",
        PrivateKey = "unit-test-private-key",
        AdpToken = "{enc:YWJj}{key:ZGVm}{iv:Z2hp}{name:QURQVG9rZW5FbmNyeXB0aW9uS2V5}{serial:Mg==}",
        Cookies =
        [
            new AudibleCookieSnapshot { Name = "session-id", Value = "cookie-one" },
            new AudibleCookieSnapshot { Name = "session-id", Value = "cookie-two" }
        ],
        DeviceSerialNumber = "test-device-serial",
        DeviceType = "test-device-type",
        AmazonAccountId = "test-account-id",
        DeviceName = "test-device",
        StoreAuthenticationCookie = "store-cookie"
    };

    // This test double models the ISecretStore encryption boundary with AES-GCM.
    // Android production encryption is implemented separately by AndroidSecretStore.
    private sealed class AesGcmSecretStore : ISecretStore
    {
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);

        public string? StoredCiphertext { get; private set; }
        public int SetCalls { get; private set; }
        public Exception? SetException { get; init; }

        public Task SetAsync(string keyName, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetCalls++;
            if (SetException is not null)
                throw SetException;

            var nonce = RandomNumberGenerator.GetBytes(12);
            var plaintext = Encoding.UTF8.GetBytes(value);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, tag.Length))
                aes.Encrypt(nonce, plaintext, ciphertext, tag);

            var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
            Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);
            StoredCiphertext = Convert.ToBase64String(payload);
            CryptographicOperations.ZeroMemory(plaintext);
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string keyName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StoredCiphertext is null)
                return Task.FromResult<string?>(null);

            var payload = Convert.FromBase64String(StoredCiphertext);
            if (payload.Length < 28)
                throw new CryptographicException("Encrypted payload is truncated.");

            var nonce = payload.AsSpan(0, 12);
            var tag = payload.AsSpan(12, 16);
            var ciphertext = payload.AsSpan(28);
            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, tag.Length))
                aes.Decrypt(nonce, ciphertext, tag, plaintext);

            var value = Encoding.UTF8.GetString(plaintext);
            CryptographicOperations.ZeroMemory(plaintext);
            return Task.FromResult<string?>(value);
        }

        public Task RemoveAsync(string keyName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoredCiphertext = null;
            return Task.CompletedTask;
        }

        public void CorruptStoredCiphertext()
        {
            var bytes = Convert.FromBase64String(StoredCiphertext!);
            bytes[^1] ^= 0x01;
            StoredCiphertext = Convert.ToBase64String(bytes);
        }
    }
}
