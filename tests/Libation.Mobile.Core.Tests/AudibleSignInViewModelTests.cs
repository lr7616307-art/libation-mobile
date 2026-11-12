using Libation.Mobile.Core;
using Libation.Mobile.UI.ViewModels;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleSignInViewModelTests
{
    [Fact]
    public async Task LoadStatus_RefreshesExpiredSessionAndRestoresConnectedState()
    {
        var secrets = new InMemorySecretStore();
        var sessions = new AccountSessionStore(secrets);
        await sessions.SaveAsync(new AccountSession(
            "account-1", "old-access", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var renewed = new AccountSession(
            "account-1", "new-access", "new-refresh", DateTimeOffset.UtcNow.AddHours(1));
        var authenticator = new FakeAuthenticator(renewed);
        var viewModel = new AudibleSignInViewModel(
            new AccountConnectionService(authenticator, sessions),
            new FakeBrowser());

        await viewModel.LoadStatusAsync();

        Assert.Equal(1, authenticator.RefreshCount);
        Assert.True(viewModel.HasConnectedAccount);
        Assert.True(viewModel.CanSyncLibrary);
        Assert.Equal("Connected", viewModel.ConnectionStatusLabel);
        var saved = await sessions.LoadAsync();
        Assert.Equal("new-access", saved?.AccessToken);
        Assert.Equal("new-refresh", saved?.RefreshToken);
    }

    [Fact]
    public async Task LoadStatus_WithoutSavedSessionDoesNotAttemptRefresh()
    {
        var authenticator = new FakeAuthenticator(null);
        var viewModel = new AudibleSignInViewModel(
            new AccountConnectionService(
                authenticator,
                new AccountSessionStore(new InMemorySecretStore())),
            new FakeBrowser());

        await viewModel.LoadStatusAsync();

        Assert.Equal(0, authenticator.RefreshCount);
        Assert.False(viewModel.HasConnectedAccount);
        Assert.Equal("Not connected", viewModel.ConnectionStatusLabel);
    }

    [Fact]
    public async Task LoadStatus_WhenRefreshFailsReportsExpiredSessionWithoutLosingSavedSession()
    {
        var secrets = new InMemorySecretStore();
        var sessions = new AccountSessionStore(secrets);
        var expired = new AccountSession(
            "account-2", "old-access", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-1));
        await sessions.SaveAsync(expired);
        var authenticator = new FakeAuthenticator(null)
        {
            RefreshException = new HttpRequestException("offline")
        };
        var viewModel = new AudibleSignInViewModel(
            new AccountConnectionService(authenticator, sessions),
            new FakeBrowser());

        await viewModel.LoadStatusAsync();

        Assert.Equal(1, authenticator.RefreshCount);
        Assert.False(viewModel.HasConnectedAccount);
        Assert.Equal("Session expired", viewModel.ConnectionStatusLabel);
        Assert.Contains("refresh failed", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        var saved = await sessions.LoadAsync();
        Assert.Equal("old-access", saved?.AccessToken);
        Assert.Equal("old-refresh", saved?.RefreshToken);
    }

    private sealed class FakeAuthenticator(AccountSession? refreshedSession) : IAccountAuthenticator
    {
        public int RefreshCount { get; private set; }
        public Exception? RefreshException { get; init; }

        public Task<ExternalSignInChallenge> BeginSignInAsync(
            AccountSignInRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ExternalSignInChallenge(
                "challenge", new Uri("https://example.invalid/login"), DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public Task<AccountSession> CompleteSignInAsync(
            string challengeId, string responseUrl, CancellationToken cancellationToken = default)
            => Task.FromException<AccountSession>(new NotSupportedException());

        public Task<AccountSession> RefreshAsync(
            AccountSession currentSession, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCount++;
            if (RefreshException is not null)
                return Task.FromException<AccountSession>(RefreshException);
            return refreshedSession is null
                ? Task.FromResult(currentSession)
                : Task.FromResult(refreshedSession);
        }
    }

    private sealed class FakeBrowser : IExternalBrowserLauncher
    {
        public Task<string?> OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
