using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleIdentityLifecycleCoordinatorTests
{
    [Fact]
    public async Task NewerLoginSupersedesOlderLateCompletion()
    {
        var coordinator = new AudibleIdentityLifecycleCoordinator();
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<string>();

        var older = coordinator.CreateAndSaveAsync(
            async _ =>
            {
                oldStarted.TrySetResult();
                await releaseOld.Task;
                return "older";
            },
            (identity, _) =>
            {
                saved.Add(identity);
                return Task.CompletedTask;
            });

        await oldStarted.Task;
        var newer = coordinator.CreateAndSaveAsync(
            _ => Task.FromResult("newer"),
            (identity, _) =>
            {
                saved.Add(identity);
                return Task.CompletedTask;
            });

        releaseOld.TrySetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(() => older);
        Assert.Equal("newer", await newer);
        Assert.Equal(new[] { "newer" }, saved);
    }

    [Fact]
    public async Task ClearInvalidatesInFlightLoginBeforeItCanPersist()
    {
        var coordinator = new AudibleIdentityLifecycleCoordinator();
        var loginStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLogin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = false;
        var cleared = false;

        var login = coordinator.CreateAndSaveAsync(
            async _ =>
            {
                loginStarted.TrySetResult();
                await releaseLogin.Task;
                return "late-login";
            },
            (_, _) =>
            {
                saved = true;
                return Task.CompletedTask;
            });

        await loginStarted.Task;
        var clear = coordinator.ClearAsync(_ =>
        {
            cleared = true;
            return Task.CompletedTask;
        });

        releaseLogin.TrySetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(() => login);
        await clear;
        Assert.False(saved);
        Assert.True(cleared);
    }

    [Fact]
    public async Task CancelledOperationDoesNotPersistIdentity()
    {
        var coordinator = new AudibleIdentityLifecycleCoordinator();
        using var cancellation = new CancellationTokenSource();
        var saved = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.CreateAndSaveAsync(
                _ =>
                {
                    cancellation.Cancel();
                    return Task.FromResult("cancelled");
                },
                (_, _) =>
                {
                    saved = true;
                    return Task.CompletedTask;
                },
                cancellation.Token));

        Assert.False(saved);
    }

    [Fact]
    public async Task SaveFailurePropagatesWithoutReturningIdentity()
    {
        var coordinator = new AudibleIdentityLifecycleCoordinator();
        var error = await Assert.ThrowsAsync<IOException>(() =>
            coordinator.CreateAndSaveAsync(
                _ => Task.FromResult("new-identity"),
                (_, _) => Task.FromException(new IOException("storage unavailable"))));

        Assert.Equal("storage unavailable", error.Message);
    }
}
