using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AccountConnectionServiceDisconnectCancellationTests
{
    [Fact]
    public async Task Disconnect_FinishesClearingSessionWhenCallerCancelsAfterLogoutStarts()
    {
        var secrets = new InMemorySecretStore();
        var sessions = new AccountSessionStore(secrets);
        await sessions.SaveAsync(new AccountSession(
            "account-1", "access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1)));
        var authenticator = new BlockingDisconnectAuthenticator();
        var service = new AccountConnectionService(authenticator, sessions);
        using var cancellation = new CancellationTokenSource();

        var disconnect = service.DisconnectAsync(cancellation.Token);
        await authenticator.DisconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        authenticator.AllowDisconnectToFinish.TrySetResult();

        await disconnect;

        Assert.Null(await sessions.LoadAsync());
        Assert.Equal(CancellationToken.None, authenticator.ReceivedCancellationToken);
    }

    private sealed class BlockingDisconnectAuthenticator : IAccountAuthenticator
    {
        public TaskCompletionSource DisconnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDisconnectToFinish { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReceivedCancellationToken { get; private set; }

        public Task<ExternalSignInChallenge> BeginSignInAsync(
            AccountSignInRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AccountSession> CompleteSignInAsync(
            string challengeId, string responseUrl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> HasRestorableIdentityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task CancelSignInAsync(string challengeId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            ReceivedCancellationToken = cancellationToken;
            DisconnectStarted.TrySetResult();
            await AllowDisconnectToFinish.Task.WaitAsync(cancellationToken);
        }

        public Task<AccountSession> RefreshAsync(
            AccountSession currentSession, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = new();

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
