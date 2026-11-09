using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AccountConnectionServiceTests
{
    [Fact]
    public async Task BeginSignIn_ValidatesRequestAndReturnsHttpsChallenge()
    {
        var authenticator = new FakeAuthenticator();
        var service = CreateService(authenticator);

        var challenge = await service.BeginSignInAsync(new("user@example.com", "in"));

        Assert.Equal("challenge-1", challenge.ChallengeId);
        Assert.Equal("https", challenge.AuthorizationUri.Scheme);
        Assert.Equal(1, authenticator.BeginCount);
    }

    [Fact]
    public async Task BeginSignIn_RejectsEmptyLoginOrMarketplace()
    {
        var service = CreateService(new FakeAuthenticator());

        await Assert.ThrowsAsync<ArgumentException>(() => service.BeginSignInAsync(new("", "in")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.BeginSignInAsync(new("user@example.com", "")));
    }

    [Fact]
    public async Task CompleteSignIn_PersistsSessionAndReturnsCredentialFreeState()
    {
        var session = NewSession("acct-1", "access-secret", "refresh-secret");
        var secrets = new InMemorySecretStore();
        var authenticator = new FakeAuthenticator(session);
        var service = new AccountConnectionService(authenticator, new AccountSessionStore(secrets));
        var challenge = await service.BeginSignInAsync(new("user@example.com", "in"));

        var state = await service.CompleteSignInAsync(challenge.ChallengeId, "https://example.invalid/return?code=redacted");

        Assert.Equal(AccountConnectionStatus.Connected, state.Status);
        Assert.Equal("acct-1", state.AccountId);
        Assert.DoesNotContain("access-secret", System.Text.Json.JsonSerializer.Serialize(state));
        Assert.DoesNotContain("refresh-secret", System.Text.Json.JsonSerializer.Serialize(state));
        Assert.Equal("acct-1", (await new AccountSessionStore(secrets).LoadAsync())?.AccountId);
        Assert.Equal(1, authenticator.CompleteCount);
    }

    [Fact]
    public async Task CompleteSignIn_RejectsUnknownChallengeWithoutCallingAuthenticator()
    {
        var authenticator = new FakeAuthenticator(NewSession("acct-1", "a", "r"));
        var service = CreateService(authenticator);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteSignInAsync("unknown", "https://example.invalid/return"));

        Assert.Equal(0, authenticator.CompleteCount);
    }

    [Fact]
    public async Task CompleteSignIn_InvalidResponseDoesNotConsumeChallenge()
    {
        var authenticator = new FakeAuthenticator(NewSession("acct-1", "a", "r"));
        var service = CreateService(authenticator);
        var challenge = await service.BeginSignInAsync(new("user@example.com", "in"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CompleteSignInAsync(challenge.ChallengeId, "http://example.invalid/return"));

        var state = await service.CompleteSignInAsync(
            challenge.ChallengeId, "https://example.invalid/return");

        Assert.Equal(AccountConnectionStatus.Connected, state.Status);
        Assert.Equal(1, authenticator.CompleteCount);
    }

    [Fact]
    public async Task CompleteSignIn_RejectsReplayAfterSuccessfulCompletion()
    {
        var authenticator = new FakeAuthenticator(NewSession("acct-1", "a", "r"));
        var service = CreateService(authenticator);
        var challenge = await service.BeginSignInAsync(new("user@example.com", "in"));

        await service.CompleteSignInAsync(challenge.ChallengeId, "https://example.invalid/return");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteSignInAsync(challenge.ChallengeId, "https://example.invalid/return"));

        Assert.Equal(1, authenticator.CompleteCount);
    }

    [Fact]
    public async Task CompleteSignIn_RejectsExpiredChallenge()
    {
        var authenticator = new FakeAuthenticator(NewSession("acct-1", "a", "r"))
        {
            ChallengeExpiry = DateTimeOffset.UtcNow.AddMilliseconds(250)
        };
        var service = CreateService(authenticator);
        var challenge = await service.BeginSignInAsync(new("user@example.com", "in"));

        // The challenge must be valid when issued, then expire before completion.
        await Task.Delay(500);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteSignInAsync(challenge.ChallengeId, "https://example.invalid/return"));

        Assert.Equal(0, authenticator.CompleteCount);
    }

    [Theory]
    [InlineData("http://example.invalid/return")]
    [InlineData("file:///tmp/return")]
    [InlineData("not-a-url")]
    public async Task CompleteSignIn_RejectsNonHttpsOrMalformedResponse(string responseUrl)
    {
        var authenticator = new FakeAuthenticator(NewSession("acct-1", "a", "r"));
        var service = CreateService(authenticator);
        var challenge = await service.BeginSignInAsync(new("user@example.com", "in"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CompleteSignInAsync(challenge.ChallengeId, responseUrl));

        Assert.Equal(0, authenticator.CompleteCount);
    }

    [Theory]
    [InlineData("https://user:password@example.invalid/return")]
    public async Task CompleteSignIn_RejectsResponseUrlWithEmbeddedCredentials(string responseUrl)
    {
        var authenticator = new FakeAuthenticator(NewSession("acct-1", "a", "r"));
        var service = CreateService(authenticator);
        var challenge = await service.BeginSignInAsync(new("user@example.com", "in"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CompleteSignInAsync(challenge.ChallengeId, responseUrl));

        Assert.Equal(0, authenticator.CompleteCount);
    }

    [Fact]
    public async Task CompleteSignIn_RejectsOversizedResponseUrlBeforeProviderCall()
    {
        var authenticator = new FakeAuthenticator(NewSession("acct-1", "a", "r"));
        var service = CreateService(authenticator);
        var challenge = await service.BeginSignInAsync(new("user@example.com", "in"));
        var responseUrl = "https://example.invalid/return?data=" + new string('a', 16 * 1024);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CompleteSignInAsync(challenge.ChallengeId, responseUrl));

        Assert.Equal(0, authenticator.CompleteCount);
    }

    [Fact]
    public async Task BeginSignIn_NewAttemptInvalidatesPreviousChallenge()
    {
        var authenticator = new FakeAuthenticator(NewSession("acct-1", "a", "r"));
        authenticator.NextChallengeId = "challenge-1";
        var service = CreateService(authenticator);
        var first = await service.BeginSignInAsync(new("user@example.com", "in"));

        authenticator.NextChallengeId = "challenge-2";
        var second = await service.BeginSignInAsync(new("user@example.com", "in"));

        Assert.NotEqual(first.ChallengeId, second.ChallengeId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteSignInAsync(first.ChallengeId, "https://example.invalid/return"));
        Assert.Equal(0, authenticator.CompleteCount);
    }

    [Fact]
    public async Task GetState_WhenNoSavedSession_IsDisconnected()
    {
        var service = CreateService(new FakeAuthenticator());

        var state = await service.GetStateAsync();

        Assert.Equal(AccountConnectionStatus.Disconnected, state.Status);
        Assert.Null(state.AccountId);
    }

    [Fact]
    public async Task GetState_WhenSessionExpired_ReportsExpired()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(NewSession("acct-2", "a", "r", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var service = new AccountConnectionService(new FakeAuthenticator(), store);

        Assert.Equal(AccountConnectionStatus.Expired, (await service.GetStateAsync()).Status);
    }

    [Fact]
    public async Task Refresh_UsesAuthenticatorAndPersistsRotatedSession()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(NewSession("acct-3", "old-access", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var authenticator = new FakeAuthenticator(NewSession("acct-3", "new-access", "new-refresh"));
        var service = new AccountConnectionService(authenticator, store);

        var state = await service.RefreshAsync();
        var saved = await store.LoadAsync();

        Assert.Equal(AccountConnectionStatus.Connected, state.Status);
        Assert.Equal("new-access", saved?.AccessToken);
        Assert.Equal("new-refresh", saved?.RefreshToken);
    }

    [Fact]
    public async Task GetState_WhenProviderIdentityCannotBeRestored_RequiresReauthentication()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(NewSession("acct-recover", "a", "r"));
        var authenticator = new FakeAuthenticator { RestorableIdentity = false };
        var service = new AccountConnectionService(authenticator, store);
        var state = await service.GetStateAsync();
        Assert.Equal(AccountConnectionStatus.ReauthenticationRequired, state.Status);
        Assert.Equal("acct-recover", state.AccountId);
    }

    [Fact]
    public async Task GetState_WhenProviderIdentityIsRestorable_ReportsConnected()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(NewSession("acct-valid", "a", "r"));
        var authenticator = new FakeAuthenticator { RestorableIdentity = true };
        var service = new AccountConnectionService(authenticator, store);
        var state = await service.GetStateAsync();
        Assert.Equal(AccountConnectionStatus.Connected, state.Status);
        Assert.Equal("acct-valid", state.AccountId);
    }

    [Fact]
    public async Task Refresh_WhenProviderIdentityCannotBeRestored_DoesNotCallRefresh()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(NewSession("acct-refresh", "a", "r"));
        var authenticator = new FakeAuthenticator { RestorableIdentity = false };
        var service = new AccountConnectionService(authenticator, store);
        var state = await service.RefreshAsync();
        Assert.Equal(AccountConnectionStatus.ReauthenticationRequired, state.Status);
        Assert.Equal(0, authenticator.RefreshCount);
        Assert.Equal("a", (await store.LoadAsync())?.AccessToken);
    }

    [Fact]
    public async Task GetState_GenericAuthenticatorWithoutSeparateIdentityRemainsConnected()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(NewSession("acct-generic", "a", "r"));
        var authenticator = new FakeAuthenticator();
        var service = new AccountConnectionService(authenticator, store);
        var state = await service.GetStateAsync();
        Assert.Equal(AccountConnectionStatus.Connected, state.Status);
    }
    [Fact]
    public async Task CancelSignIn_RemovesPendingChallengeWithoutDisconnectingSavedSession()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(NewSession("acct-cancel", "a", "r"));
        var authenticator = new FakeAuthenticator(NewSession("acct-cancel", "a", "r"));
        var service = new AccountConnectionService(authenticator, store);
        var challenge = await service.BeginSignInAsync(new("user@example.com", "in"));

        await service.CancelSignInAsync(challenge.ChallengeId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteSignInAsync(challenge.ChallengeId, "https://example.invalid/return"));
        Assert.Equal(AccountConnectionStatus.Connected, (await service.GetStateAsync()).Status);
        Assert.Equal(0, authenticator.CompleteCount);
    }

    [Fact]
    public async Task Disconnect_WaitsForInFlightRefreshThenClearsTheRefreshedSession()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(NewSession("acct-race", "old-access", "old-refresh"));
        var authenticator = new FakeAuthenticator(
            NewSession("acct-race", "new-access", "new-refresh"))
        {
            BlockRefresh = true
        };
        var service = new AccountConnectionService(authenticator, store);

        var refreshTask = service.RefreshAsync();
        await authenticator.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disconnectTask = service.DisconnectAsync();

        Assert.False(disconnectTask.IsCompleted);
        authenticator.AllowRefreshToFinish.TrySetResult();

        var refreshedState = await refreshTask;
        await disconnectTask;

        Assert.Equal(AccountConnectionStatus.Connected, refreshedState.Status);
        Assert.Null(await store.LoadAsync());
        Assert.Equal(1, authenticator.DisconnectCount);
    }

    [Fact]
    public async Task Disconnect_ClearsSavedSessionAndPendingChallenge()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(NewSession("acct-4", "a", "r"));
        var authenticator = new FakeAuthenticator();
        var service = new AccountConnectionService(authenticator, store);
        var challenge = await service.BeginSignInAsync(new("user@example.com", "in"));

        await service.DisconnectAsync();

        Assert.Equal(AccountConnectionStatus.Disconnected, (await service.GetStateAsync()).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteSignInAsync(challenge.ChallengeId, "https://example.invalid/return"));
        Assert.Equal(0, authenticator.CompleteCount);
        Assert.Equal(1, authenticator.DisconnectCount);
    }

    [Fact]
    public async Task CompleteSignIn_ConcurrentReplayOnlyCompletesOnce()
    {
        var authenticator = new FakeAuthenticator(NewSession("acct-5", "a", "r"));
        var service = CreateService(authenticator);
        var challenge = await service.BeginSignInAsync(new("user@example.com", "in"));

        async Task<bool> TryCompleteAsync()
        {
            try
            {
                await service.CompleteSignInAsync(
                    challenge.ChallengeId, "https://example.invalid/return");
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(TryCompleteAsync(), TryCompleteAsync());

        Assert.Single(results, completed => completed);
        Assert.Single(results, completed => !completed);
        Assert.Equal(1, authenticator.CompleteCount);
    }

    private static AccountConnectionService CreateService(FakeAuthenticator authenticator)
        => new(authenticator, new AccountSessionStore(new InMemorySecretStore()));

    private static AccountSession NewSession(
        string id, string access, string refresh, DateTimeOffset? expiry = null)
        => new(id, access, refresh, expiry ?? DateTimeOffset.UtcNow.AddHours(1));

    private sealed class FakeAuthenticator(AccountSession? completion = null) : IAccountAuthenticator
    {
        public int BeginCount { get; private set; }
        public int CompleteCount { get; private set; }
        public int DisconnectCount { get; private set; }
        public int RefreshCount { get; private set; }
        public bool BlockRefresh { get; init; }
        public TaskCompletionSource RefreshStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowRefreshToFinish { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool? RestorableIdentity { get; set; }
        public string NextChallengeId { get; set; } = "challenge-1";
        public DateTimeOffset ChallengeExpiry { get; set; } = DateTimeOffset.UtcNow.AddMinutes(5);

        public Task<ExternalSignInChallenge> BeginSignInAsync(
            AccountSignInRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCount++;
            return Task.FromResult(new ExternalSignInChallenge(
                NextChallengeId, new Uri("https://example.invalid/login"), ChallengeExpiry));
        }

        public Task<AccountSession> CompleteSignInAsync(
            string challengeId, string responseUrl, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompleteCount++;
            return Task.FromResult(completion ?? throw new InvalidOperationException("No fake completion result configured."));
        }

        public Task<bool> HasRestorableIdentityAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(RestorableIdentity ?? true); }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisconnectCount++;
            return Task.CompletedTask;
        }

        public async Task<AccountSession> RefreshAsync(
            AccountSession currentSession, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCount++;
            if (BlockRefresh)
            {
                RefreshStarted.TrySetResult();
                await AllowRefreshToFinish.Task.WaitAsync(cancellationToken);
            }
            return completion ?? throw new InvalidOperationException("No fake refresh result configured.");
        }
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); _values[key] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); _values.TryGetValue(key, out var value); return Task.FromResult(value); }
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); _values.Remove(key); return Task.CompletedTask; }
    }
}
