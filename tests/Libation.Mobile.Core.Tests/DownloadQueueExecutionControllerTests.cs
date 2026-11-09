using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class DownloadQueueExecutionControllerTests
{
    [Fact]
    public async Task Cancel_RacingWithRunCompletion_IsSafe()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        var worker = new DownloadQueueWorker(
            queue,
            new AudioFileDownloader(client),
            new EmptySource());
        var controller = new DownloadQueueExecutionController(worker);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var run = controller.RunAsync();
            var cancel = Task.Run(controller.Cancel);

            // Cancellation can win before the empty batch completes, or the batch
            // can finish first. Either outcome is valid; Cancel must never throw
            // because the controller disposed its completed run token source.
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
                // Expected when Cancel wins the race.
            }

            await cancel;
            Assert.False(controller.IsRunning);
        }
    }

    private sealed class EmptySource : IAuthorizedAudioDownloadSource
    {
        public Task<AuthorizedAudioDownload> ResolveAsync(
            string audiobookId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("An empty queue must not resolve a source.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "LibationMobileControllerTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
