using System.Security.Cryptography;
using AudibleApi;
using AudibleApi.Authorization;
using Dinah.Core;
using Dinah.Core.Security;
using Libation.Mobile.Audible;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleIdentityMaintainerTests
{
    [Fact]
    public async Task ValidToken_DoesNotRefreshOrPersist()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var initialSnapshot = CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddHours(1));
        var identity = AudibleIdentitySnapshotMapper.Restore(initialSnapshot);
        var authorize = new FakeRefresher(_ => Task.FromException<AudibleTokenRefreshResult>(new Xunit.Sdk.XunitException("Refresh should not be called.")));
        var secrets = new RecordingSecretStore();
        secrets.Seed(initialSnapshot);
        var maintainer = new AudibleIdentityMaintainer(identity, authorize, new FixedClock(now), new AudibleIdentitySnapshotStore(secrets));

        var token = await maintainer.GetAccessTokenAsync();

        Assert.NotNull(token);
        Assert.Equal("Atna|test-access", token!.Reveal());
        Assert.Equal(0, authorize.RefreshCalls);
        Assert.Equal(0, secrets.SetCalls);
    }

    [Fact]
    public async Task ExpiredToken_IsPersistedBeforeNewTokenIsReturned()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var initialSnapshot = CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddMinutes(-1));
        var identity = AudibleIdentitySnapshotMapper.Restore(initialSnapshot);
        var secrets = new RecordingSecretStore();
        secrets.Seed(initialSnapshot);
        var authorize = new FakeRefresher(_ => Task.FromResult(new AudibleTokenRefreshResult(new AccessToken(new SecretString("Atna|refreshed"), now.AddHours(2)), null)));
        var maintainer = new AudibleIdentityMaintainer(identity, authorize, new FixedClock(now), new AudibleIdentitySnapshotStore(secrets));

        var token = await maintainer.GetAccessTokenAsync();

        Assert.NotNull(token);
        Assert.Equal("Atna|refreshed", token!.Reveal());
        Assert.Equal(1, authorize.RefreshCalls);
        Assert.Equal(1, secrets.SetCalls);
        Assert.NotNull(secrets.LastValue);
        var saved = AudibleIdentitySnapshotSerializer.Deserialize(secrets.LastValue!);
        Assert.Equal("Atna|refreshed", saved.AccessToken);
    }

    [Fact]
    public async Task PersistenceFailure_RestoresPreviousInMemoryToken()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var initialSnapshot = CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddMinutes(-1));
        var identity = AudibleIdentitySnapshotMapper.Restore(initialSnapshot);
        var secrets = new RecordingSecretStore { SetException = new IOException("simulated storage failure") };
        secrets.Seed(initialSnapshot);
        var authorize = new FakeRefresher(_ => Task.FromResult(new AudibleTokenRefreshResult(new AccessToken(new SecretString("Atna|refreshed"), now.AddHours(2)), null)));
        var maintainer = new AudibleIdentityMaintainer(identity, authorize, new FixedClock(now), new AudibleIdentitySnapshotStore(secrets));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => maintainer.GetAccessTokenAsync());

        Assert.Contains("securely saved", error.Message);
        Assert.Equal("Atna|test-access", identity.ExistingAccessToken.Reveal());
        Assert.Equal(1, authorize.RefreshCalls);
        Assert.Equal(1, secrets.SetCalls);
    }

    [Fact]
    public async Task ConcurrentExpiredTokenRequests_RefreshOnlyOnce()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var initialSnapshot = CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddMinutes(-1));
        var identity = AudibleIdentitySnapshotMapper.Restore(initialSnapshot);
        var secrets = new RecordingSecretStore();
        secrets.Seed(initialSnapshot);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorize = new FakeRefresher(async _ =>
        {
            refreshStarted.TrySetResult();
            await allowRefresh.Task;
            return new AudibleTokenRefreshResult(new AccessToken(new SecretString("Atna|refreshed"), now.AddHours(2)), null);
        });
        var maintainer = new AudibleIdentityMaintainer(identity, authorize, new FixedClock(now), new AudibleIdentitySnapshotStore(secrets));

        var first = maintainer.GetAccessTokenAsync();
        await refreshStarted.Task;
        var second = maintainer.GetAccessTokenAsync();
        var third = maintainer.GetAccessTokenAsync();
        allowRefresh.TrySetResult();

        var tokens = await Task.WhenAll(first, second, third);

        Assert.All(tokens, token => Assert.Equal("Atna|refreshed", token!.Reveal()));
        Assert.Equal(1, authorize.RefreshCalls);
        Assert.Equal(1, secrets.SetCalls);
    }

    [Fact]
    public async Task Disconnect_WaitsForRefreshThenClearsIdentityAndRejectsFurtherAccess()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var initialSnapshot = CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddMinutes(-1));
        var identity = AudibleIdentitySnapshotMapper.Restore(initialSnapshot);
        var secrets = new RecordingSecretStore();
        secrets.Seed(initialSnapshot);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorize = new FakeRefresher(async _ =>
        {
            refreshStarted.TrySetResult();
            await allowRefresh.Task;
            return new AudibleTokenRefreshResult(new AccessToken(new SecretString("Atna|refreshed"), now.AddHours(2)), null);
        });
        var maintainer = new AudibleIdentityMaintainer(identity, authorize, new FixedClock(now), new AudibleIdentitySnapshotStore(secrets));

        var refresh = maintainer.GetAccessTokenAsync();
        await refreshStarted.Task;
        var disconnect = maintainer.DisconnectAsync();
        allowRefresh.TrySetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(() => refresh);
        await disconnect;

        Assert.Equal(0, secrets.SetCalls);
        Assert.Equal(1, secrets.RemoveCalls);
        Assert.Null(secrets.LastValue);
        await Assert.ThrowsAsync<OperationCanceledException>(() => maintainer.GetAccessTokenAsync());
        Assert.Equal(1, authorize.RefreshCalls);
    }

    [Fact]
    public async Task DisconnectPersistenceFailure_PreservesSnapshotButInvalidatesOldMaintainer()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var initialSnapshot = CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddHours(1));
        var identity = AudibleIdentitySnapshotMapper.Restore(initialSnapshot);
        var secrets = new RecordingSecretStore { RemoveException = new IOException("simulated storage failure") };
        secrets.Seed(initialSnapshot);
        var authorize = new FakeRefresher(_ => Task.FromException<AudibleTokenRefreshResult>(new Xunit.Sdk.XunitException("Refresh should not be called.")));
        var maintainer = new AudibleIdentityMaintainer(identity, authorize, new FixedClock(now), new AudibleIdentitySnapshotStore(secrets));

        await Assert.ThrowsAsync<IOException>(() => maintainer.DisconnectAsync());

        await Assert.ThrowsAsync<OperationCanceledException>(() => maintainer.GetAccessTokenAsync());
        Assert.NotNull(secrets.LastValue);
        Assert.Equal("Atna|test-access", AudibleIdentitySnapshotSerializer.Deserialize(secrets.LastValue!).AccessToken);
        Assert.Equal(1, secrets.RemoveCalls);
        Assert.Equal(0, authorize.RefreshCalls);

        // A fresh API session can still restore the identity that the failed clear left persisted.
        var restoredSnapshot = AudibleIdentitySnapshotSerializer.Deserialize(secrets.LastValue!);
        var restoredIdentity = AudibleIdentitySnapshotMapper.Restore(restoredSnapshot);
        var restoredMaintainer = new AudibleIdentityMaintainer(
            restoredIdentity,
            new FakeRefresher(_ => Task.FromException<AudibleTokenRefreshResult>(
                new Xunit.Sdk.XunitException("The saved token is still valid."))),
            new FixedClock(now),
            new AudibleIdentitySnapshotStore(secrets));
        var restoredToken = await restoredMaintainer.GetAccessTokenAsync();
        Assert.Equal("Atna|test-access", restoredToken!.Reveal());
    }

    [Fact]
    public async Task CachedTokenRequest_AfterDisconnect_IsRejected()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var initialSnapshot = CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddHours(1));
        var identity = AudibleIdentitySnapshotMapper.Restore(initialSnapshot);
        var secrets = new RecordingSecretStore();
        secrets.Seed(initialSnapshot);
        var authorize = new FakeRefresher(_ => Task.FromException<AudibleTokenRefreshResult>(new Xunit.Sdk.XunitException("Refresh should not be called.")));
        var maintainer = new AudibleIdentityMaintainer(identity, authorize, new FixedClock(now), new AudibleIdentitySnapshotStore(secrets));

        await maintainer.DisconnectAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => maintainer.GetAccessTokenAsync());
        Assert.Equal(1, secrets.RemoveCalls);
        Assert.Equal(0, authorize.RefreshCalls);
    }

    [Fact]
    public async Task CancelledDisconnectWhileWaitingForRefresh_DoesNotClearIdentity()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var initialSnapshot = CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddMinutes(-1));
        var identity = AudibleIdentitySnapshotMapper.Restore(initialSnapshot);
        var secrets = new RecordingSecretStore();
        secrets.Seed(initialSnapshot);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorize = new FakeRefresher(async _ =>
        {
            refreshStarted.TrySetResult();
            await allowRefresh.Task;
            return new AudibleTokenRefreshResult(new AccessToken(new SecretString("Atna|refreshed"), now.AddHours(2)), null);
        });
        var maintainer = new AudibleIdentityMaintainer(identity, authorize, new FixedClock(now), new AudibleIdentitySnapshotStore(secrets));

        var refresh = maintainer.GetAccessTokenAsync();
        await refreshStarted.Task;
        using var cancellation = new CancellationTokenSource();
        var disconnect = maintainer.DisconnectAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnect);
        allowRefresh.TrySetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => refresh);
        await Assert.ThrowsAsync<OperationCanceledException>(() => maintainer.GetAccessTokenAsync());

        Assert.Equal(0, secrets.RemoveCalls);
        Assert.Equal(0, secrets.SetCalls);
        Assert.Equal(1, authorize.RefreshCalls);

        // Cancellation leaves the encrypted snapshot intact, but the old in-memory
        // maintainer is invalidated. A newly restored API session can retry safely.
        var restoredSnapshot = AudibleIdentitySnapshotSerializer.Deserialize(secrets.LastValue!);
        var restoredIdentity = AudibleIdentitySnapshotMapper.Restore(restoredSnapshot);
        var retryRefresher = new FakeRefresher(_ => Task.FromResult(new AudibleTokenRefreshResult(
            new AccessToken(new SecretString("Atna|refreshed"), now.AddHours(2)), null)));
        var retryMaintainer = new AudibleIdentityMaintainer(
            restoredIdentity, retryRefresher, new FixedClock(now), new AudibleIdentitySnapshotStore(secrets));
        var token = await retryMaintainer.GetAccessTokenAsync();

        Assert.Equal("Atna|refreshed", token!.Reveal());
        Assert.Equal(1, secrets.SetCalls);
        Assert.Equal(1, retryRefresher.RefreshCalls);
    }

    [Fact]
    public async Task SeparateMaintainers_UseRotatedTokensSavedByFirstMaintainer()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var initialSnapshot = CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddMinutes(-1));
        var secrets = new RecordingSecretStore();
        secrets.Seed(initialSnapshot);
        var snapshotStore = new AudibleIdentitySnapshotStore(secrets);
        var firstIdentity = AudibleIdentitySnapshotMapper.Restore(initialSnapshot);
        var secondIdentity = AudibleIdentitySnapshotMapper.Restore(initialSnapshot);
        var firstRefresher = new FakeRefresher(token =>
        {
            Assert.Equal("Atnr|test-refresh", token.Reveal());
            return Task.FromResult(new AudibleTokenRefreshResult(
                new AccessToken(new SecretString("Atna|rotated-access"), now.AddHours(2)),
                new RefreshToken(new SecretString("Atnr|rotated-refresh"))));
        });
        var secondRefresher = new FakeRefresher(_ =>
            Task.FromException<AudibleTokenRefreshResult>(
                new Xunit.Sdk.XunitException("A second refresh with the stale token must not occur.")));
        var firstMaintainer = new AudibleIdentityMaintainer(
            firstIdentity, firstRefresher, new FixedClock(now), snapshotStore);
        var secondMaintainer = new AudibleIdentityMaintainer(
            secondIdentity, secondRefresher, new FixedClock(now), snapshotStore);

        var firstToken = await firstMaintainer.GetAccessTokenAsync();
        var secondToken = await secondMaintainer.GetAccessTokenAsync();

        Assert.Equal("Atna|rotated-access", firstToken!.Reveal());
        Assert.Equal("Atna|rotated-access", secondToken!.Reveal());
        Assert.Equal("Atnr|rotated-refresh", secondIdentity.RefreshToken!.Reveal());
        Assert.Equal(1, firstRefresher.RefreshCalls);
        Assert.Equal(0, secondRefresher.RefreshCalls);
    }

    [Fact]
    public async Task ExistingMaintainer_IsInvalidatedAfterAnotherLoginReplacesIdentity()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var initialSnapshot = CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddHours(1));
        var secrets = new RecordingSecretStore();
        secrets.Seed(initialSnapshot);
        var snapshotStore = new AudibleIdentitySnapshotStore(secrets);
        var identity = AudibleIdentitySnapshotMapper.Restore(initialSnapshot);
        var maintainer = new AudibleIdentityMaintainer(
            identity,
            new FakeRefresher(_ => Task.FromException<AudibleTokenRefreshResult>(
                new Xunit.Sdk.XunitException("A replaced identity must not refresh."))),
            new FixedClock(now),
            snapshotStore);
        var replacementSnapshot = initialSnapshot with
        {
            DeviceSerialNumber = "replacement-device-serial",
            AccessToken = "Atna|replacement-access",
            RefreshToken = "Atnr|replacement-refresh"
        };

        await snapshotStore.LifecycleCoordinator.CreateAndSaveAsync(
            _ => Task.FromResult(replacementSnapshot),
            (snapshot, cancellationToken) => snapshotStore.SaveAsync(snapshot, cancellationToken));

        await Assert.ThrowsAsync<OperationCanceledException>(() => maintainer.GetAccessTokenAsync());
    }

    [Fact]
    public async Task ExistingMaintainer_IsInvalidatedAfterAuthenticatorStyleDisconnect()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var initialSnapshot = CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddHours(1));
        var secrets = new RecordingSecretStore();
        secrets.Seed(initialSnapshot);
        var snapshotStore = new AudibleIdentitySnapshotStore(secrets);
        var identity = AudibleIdentitySnapshotMapper.Restore(initialSnapshot);
        var maintainer = new AudibleIdentityMaintainer(
            identity,
            new FakeRefresher(_ => Task.FromException<AudibleTokenRefreshResult>(
                new Xunit.Sdk.XunitException("A disconnected identity must not refresh."))),
            new FixedClock(now),
            snapshotStore);

        await snapshotStore.LifecycleCoordinator.ClearAsync(
            token => snapshotStore.ClearAsync(token));

        await Assert.ThrowsAsync<OperationCanceledException>(() => maintainer.GetAccessTokenAsync());
    }

    [Fact]
    public async Task RefreshFailure_LeavesExistingIdentityAndDoesNotRegisterOrDeregister()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var initialSnapshot = CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddMinutes(-1));
        var identity = AudibleIdentitySnapshotMapper.Restore(initialSnapshot);
        var authorize = new FakeRefresher(_ => Task.FromException<AudibleTokenRefreshResult>(new HttpRequestException("simulated refresh failure")));
        var secrets = new RecordingSecretStore();
        secrets.Seed(initialSnapshot);
        var maintainer = new AudibleIdentityMaintainer(identity, authorize, new FixedClock(now), new AudibleIdentitySnapshotStore(secrets));

        await Assert.ThrowsAsync<HttpRequestException>(() => maintainer.GetAccessTokenAsync());

        Assert.Equal("Atna|test-access", identity.ExistingAccessToken.Reveal());
        Assert.Equal(1, authorize.RefreshCalls);
        Assert.Equal(0, secrets.SetCalls);
    }

    [Fact]
    public async Task RotatedRefreshToken_IsPersistedWithNewAccessToken()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var initialSnapshot = CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddMinutes(-1));
        var identity = AudibleIdentitySnapshotMapper.Restore(initialSnapshot);
        var secrets = new RecordingSecretStore();
        secrets.Seed(initialSnapshot);
        var refresher = new FakeRefresher(_ => Task.FromResult(new AudibleTokenRefreshResult(
            new AccessToken(new SecretString("Atna|refreshed"), now.AddHours(2)),
            new RefreshToken(new SecretString("Atnr|rotated")))));
        var maintainer = new AudibleIdentityMaintainer(
            identity, refresher, new FixedClock(now), new AudibleIdentitySnapshotStore(secrets));

        var accessToken = await maintainer.GetAccessTokenAsync();

        Assert.Equal("Atna|refreshed", accessToken!.Reveal());
        Assert.Equal("Atnr|rotated", identity.RefreshToken!.Reveal());
        var saved = AudibleIdentitySnapshotSerializer.Deserialize(secrets.LastValue!);
        Assert.Equal("Atna|refreshed", saved.AccessToken);
        Assert.Equal("Atnr|rotated", saved.RefreshToken);
        Assert.Equal(AudibleOAuthFlow.RegistrationProfileId, saved.RegistrationProfileId);
    }

    private static AudibleIdentitySnapshot CreateSnapshot(string privateKey, DateTime expiry) => new()
    {
        SchemaVersion = AudibleIdentitySnapshot.CurrentSchemaVersion,
        Provider = AudibleIdentitySnapshot.ProviderName,
        AudibleApiVersion = AudibleIdentitySnapshot.SupportedAudibleApiVersion,
        RegistrationProfileId = AudibleOAuthFlow.RegistrationProfileId,
        LocaleName = "us",
        AccessToken = "Atna|test-access",
        AccessTokenExpiresAtUtc = new DateTimeOffset(expiry),
        RefreshToken = "Atnr|test-refresh",
        PrivateKey = privateKey,
        AdpToken = "{enc:YWJj}{key:ZGVm}{iv:Z2hp}{name:QURQVG9rZW5FbmNyeXB0aW9uS2V5}{serial:Mg==}",
        Cookies = [],
        StoreAuthenticationCookie = "store-cookie",
        DeviceSerialNumber = "test-device-serial",
        DeviceType = "test-device-type",
        AmazonAccountId = "test-account"
    };

    private sealed class FixedClock(DateTime utcNow) : ISystemDateTime
    {
        public DateTime Now => utcNow.ToLocalTime();
        public DateTime UtcNow => utcNow;
    }

    private sealed class RecordingSecretStore : ISecretStore
    {
        public int SetCalls { get; private set; }

        public void Seed(AudibleIdentitySnapshot snapshot)
            => LastValue = AudibleIdentitySnapshotSerializer.Serialize(snapshot);
        public int RemoveCalls { get; private set; }
        public string? LastValue { get; private set; }
        public Exception? SetException { get; init; }
        public Exception? RemoveException { get; init; }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            SetCalls++;
            if (SetException is not null)
                throw SetException;
            LastValue = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(LastValue);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            RemoveCalls++;
            if (RemoveException is not null)
                throw RemoveException;
            LastValue = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRefresher(Func<RefreshToken, Task<AudibleTokenRefreshResult>> refresh)
        : IAudibleTokenRefresher
    {
        public int RefreshCalls { get; private set; }

        public Task<AudibleTokenRefreshResult> RefreshAsync(
            Locale locale,
            RefreshToken refreshToken,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCalls++;
            return refresh(refreshToken);
        }
    }
}
