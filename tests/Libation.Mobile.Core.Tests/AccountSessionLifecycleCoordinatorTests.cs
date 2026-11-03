using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AccountSessionLifecycleCoordinatorTests
{
    [Fact]
    public async Task NewSignIn_InvalidatesOlderLateCompletion()
    {
        var coordinator = new AccountSessionLifecycleCoordinator();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.CreateAndSaveAsync(
            async _ =>
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
                return "old";
            },
            (_, _) => Task.CompletedTask);
        await firstStarted.Task;

        var second = coordinator.CreateAndSaveAsync(
            _ => Task.FromResult("new"),
            (_, _) => Task.CompletedTask);
        releaseFirst.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("new", await second);
    }

    [Fact]
    public async Task Disconnect_InvalidatesInFlightRefreshBeforeCommit()
    {
        var coordinator = new AccountSessionLifecycleCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = false;
        var refresh = coordinator.ExecuteCurrentAsync(
            async _ =>
            {
                started.SetResult();
                await release.Task;
                return "stale";
            },
            (_, _) => { committed = true; return Task.CompletedTask; });
        await started.Task;

        var cleared = false;
        var disconnect = coordinator.ClearAsync(_ => { cleared = true; return Task.CompletedTask; });
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        await disconnect;
        Assert.False(committed);
        Assert.True(cleared);
    }

    [Fact]
    public async Task CancellationWhileWaiting_DoesNotRunQueuedCommit()
    {
        var coordinator = new AccountSessionLifecycleCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.CreateAndSaveAsync(
            async _ => { started.SetResult(); await release.Task; return 1; },
            (_, _) => Task.CompletedTask);
        await started.Task;

        using var cancellation = new CancellationTokenSource();
        var second = coordinator.ExecuteCurrentAsync(
            _ => Task.FromResult(2),
            (_, _) => Task.CompletedTask,
            cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        release.SetResult();
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task SaveFailure_Propagates()
    {
        var coordinator = new AccountSessionLifecycleCoordinator();

        await Assert.ThrowsAsync<IOException>(() => coordinator.CreateAndSaveAsync(
            _ => Task.FromResult("session"),
            (_, _) => throw new IOException("test storage failure")));
    }
}
