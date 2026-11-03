using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class ConcurrentAccountRefreshTests
{
    [Fact]
    public async Task ConcurrentRefreshes_SecondUsesFirstRotatedSession()
    {
        var secrets = new MemorySecrets();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(Session("access-1", "refresh-1"));
        var authenticator = new RotatingAuthenticator();
        var service = new AccountConnectionService(authenticator, store);

        var first = service.RefreshAsync();
        await authenticator.FirstRefreshStarted.Task;
        var second = service.RefreshAsync();
        authenticator.ReleaseFirstRefresh.TrySetResult();

        await Task.WhenAll(first, second);

        Assert.Equal(new[] { "refresh-1", "refresh-2" }, authenticator.ReceivedRefreshTokens);
        var saved = await store.LoadAsync();
        Assert.Equal("refresh-3", saved?.RefreshToken);
        Assert.Equal("access-3", saved?.AccessToken);
    }

    private static AccountSession Session(string access, string refresh)
        => new("account", access, refresh, DateTimeOffset.UtcNow.AddHours(1));

    private sealed class RotatingAuthenticator : IAccountAuthenticator
    {
        private int calls;
        private readonly List<string> received = new();
        public TaskCompletionSource FirstRefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> ReceivedRefreshTokens { get { lock (received) return received.ToArray(); } }

        public Task<ExternalSignInChallenge> BeginSignInAsync(AccountSignInRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AccountSession> CompleteSignInAsync(string challengeId, string responseUrl, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task<AccountSession> RefreshAsync(AccountSession currentSession, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref calls);
            lock (received) received.Add(currentSession.RefreshToken);
            if (call == 1)
            {
                FirstRefreshStarted.TrySetResult();
                await ReleaseFirstRefresh.Task.WaitAsync(cancellationToken);
                return Session("access-2", "refresh-2");
            }
            return Session("access-3", "refresh-3");
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> values = new();
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); values[key] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); values.TryGetValue(key, out var value); return Task.FromResult(value); }
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); values.Remove(key); return Task.CompletedTask; }
    }
}
