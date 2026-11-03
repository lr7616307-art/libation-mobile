using System.Security.Cryptography;
using AudibleApi;
using AudibleApi.Authorization;
using Dinah.Core;
using Dinah.Core.Security;
using Libation.Mobile.Audible;
using Libation.Mobile.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleIdentityMaintainerTests
{
    [Fact]
    public async Task ValidToken_DoesNotRefreshOrPersist()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var identity = AudibleIdentitySnapshotMapper.Restore(CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddHours(1)));
        var authorize = new FakeAuthorize(_ => Task.FromException<AccessToken>(new Xunit.Sdk.XunitException("Refresh should not be called.")));
        var secrets = new RecordingSecretStore();
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
        var identity = AudibleIdentitySnapshotMapper.Restore(CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddMinutes(-1)));
        var secrets = new RecordingSecretStore();
        var authorize = new FakeAuthorize(_ => Task.FromResult(new AccessToken(new SecretString("Atna|refreshed"), now.AddHours(2))));
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
        var identity = AudibleIdentitySnapshotMapper.Restore(CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddMinutes(-1)));
        var secrets = new RecordingSecretStore { SetException = new IOException("simulated storage failure") };
        var authorize = new FakeAuthorize(_ => Task.FromResult(new AccessToken(new SecretString("Atna|refreshed"), now.AddHours(2))));
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
        var identity = AudibleIdentitySnapshotMapper.Restore(CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddMinutes(-1)));
        var secrets = new RecordingSecretStore();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorize = new FakeAuthorize(async _ =>
        {
            refreshStarted.TrySetResult();
            await allowRefresh.Task;
            return new AccessToken(new SecretString("Atna|refreshed"), now.AddHours(2));
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
        var identity = AudibleIdentitySnapshotMapper.Restore(CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddMinutes(-1)));
        var secrets = new RecordingSecretStore();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorize = new FakeAuthorize(async _ =>
        {
            refreshStarted.TrySetResult();
            await allowRefresh.Task;
            return new AccessToken(new SecretString("Atna|refreshed"), now.AddHours(2));
        });
        var maintainer = new AudibleIdentityMaintainer(identity, authorize, new FixedClock(now), new AudibleIdentitySnapshotStore(secrets));

        var refresh = maintainer.GetAccessTokenAsync();
        await refreshStarted.Task;
        var disconnect = maintainer.DisconnectAsync();
        allowRefresh.TrySetResult();

        var token = await refresh;
        await disconnect;

        Assert.Equal("Atna|refreshed", token!.Reveal());
        Assert.Equal(1, secrets.SetCalls);
        Assert.Equal(1, secrets.RemoveCalls);
        Assert.Null(secrets.LastValue);
        await Assert.ThrowsAsync<InvalidOperationException>(() => maintainer.GetAccessTokenAsync());
        Assert.Equal(1, authorize.RefreshCalls);
    }

    [Fact]
    public async Task DisconnectPersistenceFailure_DoesNotMarkSessionDisconnected()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var identity = AudibleIdentitySnapshotMapper.Restore(CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddHours(1)));
        var secrets = new RecordingSecretStore { RemoveException = new IOException("simulated storage failure") };
        var authorize = new FakeAuthorize(_ => Task.FromException<AccessToken>(new Xunit.Sdk.XunitException("Refresh should not be called.")));
        var maintainer = new AudibleIdentityMaintainer(identity, authorize, new FixedClock(now), new AudibleIdentitySnapshotStore(secrets));

        await Assert.ThrowsAsync<IOException>(() => maintainer.DisconnectAsync());

        var token = await maintainer.GetAccessTokenAsync();
        Assert.Equal("Atna|test-access", token!.Reveal());
        Assert.Equal(1, secrets.RemoveCalls);
        Assert.Equal(0, authorize.RefreshCalls);
    }

    [Fact]
    public async Task CancelledDisconnectWhileWaitingForRefresh_DoesNotClearIdentity()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var identity = AudibleIdentitySnapshotMapper.Restore(CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddMinutes(-1)));
        var secrets = new RecordingSecretStore();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorize = new FakeAuthorize(async _ =>
        {
            refreshStarted.TrySetResult();
            await allowRefresh.Task;
            return new AccessToken(new SecretString("Atna|refreshed"), now.AddHours(2));
        });
        var maintainer = new AudibleIdentityMaintainer(identity, authorize, new FixedClock(now), new AudibleIdentitySnapshotStore(secrets));

        var refresh = maintainer.GetAccessTokenAsync();
        await refreshStarted.Task;
        using var cancellation = new CancellationTokenSource();
        var disconnect = maintainer.DisconnectAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnect);
        allowRefresh.TrySetResult();
        await refresh;

        var token = await maintainer.GetAccessTokenAsync();
        Assert.Equal("Atna|refreshed", token!.Reveal());
        Assert.Equal(0, secrets.RemoveCalls);
        Assert.Equal(1, secrets.SetCalls);
    }

    [Fact]
    public async Task RefreshFailure_LeavesExistingIdentityAndDoesNotRegisterOrDeregister()
    {
        using var rsa = RSA.Create(2048);
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var identity = AudibleIdentitySnapshotMapper.Restore(CreateSnapshot(rsa.ExportRSAPrivateKeyPem(), now.AddMinutes(-1)));
        var authorize = new FakeAuthorize(_ => Task.FromException<AccessToken>(new HttpRequestException("simulated refresh failure")));
        var secrets = new RecordingSecretStore();
        var maintainer = new AudibleIdentityMaintainer(identity, authorize, new FixedClock(now), new AudibleIdentitySnapshotStore(secrets));

        await Assert.ThrowsAsync<HttpRequestException>(() => maintainer.GetAccessTokenAsync());

        Assert.Equal("Atna|test-access", identity.ExistingAccessToken.Reveal());
        Assert.Equal(1, authorize.RefreshCalls);
        Assert.Equal(0, secrets.SetCalls);
    }

    private static AudibleIdentitySnapshot CreateSnapshot(string privateKey, DateTime expiry) => new()
    {
        SchemaVersion = AudibleIdentitySnapshot.CurrentSchemaVersion,
        Provider = AudibleIdentitySnapshot.ProviderName,
        AudibleApiVersion = AudibleIdentitySnapshot.SupportedAudibleApiVersion,
        LocaleName = "us",
        AccessToken = "Atna|test-access",
        AccessTokenExpiresAtUtc = new DateTimeOffset(expiry),
        RefreshToken = "Atnr|test-refresh",
        PrivateKey = privateKey,
        AdpToken = "{enc:YWJj}{key:ZGVm}{iv:Z2hp}{name:QURQVG9rZW5FbmNyeXB0aW9uS2V5}{serial:Mg==}",
        Cookies = [],
        StoreAuthenticationCookie = "store-cookie"
    };

    private sealed class FixedClock(DateTime utcNow) : ISystemDateTime
    {
        public DateTime Now => utcNow.ToLocalTime();
        public DateTime UtcNow => utcNow;
    }

    private sealed class RecordingSecretStore : ISecretStore
    {
        public int SetCalls { get; private set; }
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

    private sealed class FakeAuthorize(Func<RefreshToken, Task<AccessToken>> refresh) : IAuthorize
    {
        public int RefreshCalls { get; private set; }

        public Task<AccessToken> RefreshAccessTokenAsync(RefreshToken refreshToken)
        {
            RefreshCalls++;
            return refresh(refreshToken);
        }

        public Task<JObject> RegisterAsync(OAuth2 authorization) =>
            throw new NotSupportedException("Registration is not part of token maintenance tests.");

        public Task<bool> DeregisterAsync(AccessToken accessToken, IEnumerable<KeyValuePair<string, SecretString>>? cookies) =>
            throw new NotSupportedException("Deregistration is not part of token maintenance tests.");

        public Task<AccessToken> ExtractAccessTokenAsync(HttpResponseMessage response) =>
            throw new NotSupportedException("Token extraction is not part of token maintenance tests.");
    }
}
