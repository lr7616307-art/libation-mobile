using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleOfflineDownloadPipelineTests
{
    [Fact]
    public async Task ExecuteAsync_SkipsWorkWhenLicenceAndMediaAreAlreadyComplete()
    {
        var backend = new FakeBackend { HasLicense = true, HasMedia = true };
        var stages = new CaptureProgress<AudibleOfflineDownloadStage>();

        await new AudibleOfflineDownloadPipeline().ExecuteAsync(
            "B012345678", backend, stages);

        Assert.Equal(0, backend.AcquireCalls);
        Assert.Equal(0, backend.TransferCalls);
        Assert.Equal("completed", stages.Values[^1].Stage);
    }

    [Fact]
    public async Task ExecuteAsync_AcquiresLicenceThenTransfersAndVerifies()
    {
        var backend = new FakeBackend();
        var stages = new CaptureProgress<AudibleOfflineDownloadStage>();
        var transfer = new CaptureProgress<WidevineOfflineDownloadProgress>();

        await new AudibleOfflineDownloadPipeline().ExecuteAsync(
            "B012345678", backend, stages, transfer);

        Assert.Equal(1, backend.AcquireCalls);
        Assert.Equal(1, backend.TransferCalls);
        Assert.Contains(stages.Values, item => item.Stage == "authorizing");
        Assert.Contains(stages.Values, item => item.Stage == "transferring");
        Assert.Contains(stages.Values, item => item.Stage == "test-transfer");
        Assert.Contains(stages.Values, item => item.Stage == "verifying");
        Assert.Equal("completed", stages.Values[^1].Stage);
        Assert.Single(transfer.Values);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotTransferWhenProviderLicenceFails()
    {
        var backend = new FakeBackend { AcquireException = new InvalidOperationException("Licence denied.") };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AudibleOfflineDownloadPipeline().ExecuteAsync("B012345678", backend));

        Assert.Equal(1, backend.AcquireCalls);
        Assert.Equal(0, backend.TransferCalls);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsMediaThatFailsFinalVerification()
    {
        var backend = new FakeBackend { CompleteMediaAfterTransfer = false };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AudibleOfflineDownloadPipeline().ExecuteAsync("B012345678", backend));

        Assert.Equal(1, backend.TransferCalls);
    }

    [Fact]
    public async Task ExecuteAsync_UsesBoundedRetryForTransientTransferFailures()
    {
        var backend = new FakeBackend { TransferFailuresRemaining = 1 };
        var stages = new CaptureProgress<AudibleOfflineDownloadStage>();

        await new AudibleOfflineDownloadPipeline().ExecuteAsync(
            "B012345678",
            backend,
            stages,
            isRetryableTransferFailure: exception => exception is HttpRequestException);

        Assert.Equal(2, backend.TransferCalls);
        Assert.Contains(stages.Values, item => item.Stage == "retrying");
    }

    private sealed class FakeBackend : IAudibleOfflineDownloadBackend
    {
        public bool HasLicense { get; set; }
        public bool HasMedia { get; set; }
        public bool CompleteMediaAfterTransfer { get; set; } = true;
        public int AcquireCalls { get; private set; }
        public int TransferCalls { get; private set; }
        public int TransferFailuresRemaining { get; set; }
        public Exception? AcquireException { get; set; }

        public Task<bool> HasOfflineLicenseAsync(string asin, CancellationToken cancellationToken = default)
            => Task.FromResult(HasLicense);

        public Task AcquireOfflineLicenseAsync(
            string asin,
            IProgress<AudibleOfflineDownloadStage>? stageProgress,
            CancellationToken cancellationToken = default)
        {
            AcquireCalls++;
            if (AcquireException is not null)
                throw AcquireException;
            HasLicense = true;
            return Task.CompletedTask;
        }

        public Task<bool> HasOfflineMediaAsync(string asin, CancellationToken cancellationToken = default)
            => Task.FromResult(HasMedia);

        public Task DownloadOfflineMediaWithProgressAsync(
            string asin,
            IProgress<WidevineOfflineDownloadProgress>? progress,
            IProgress<AudibleOfflineDownloadStage>? stageProgress,
            CancellationToken cancellationToken = default)
        {
            TransferCalls++;
            stageProgress?.Report(new AudibleOfflineDownloadStage(
                "test-transfer", "Test transfer stage."));
            if (TransferFailuresRemaining > 0)
            {
                TransferFailuresRemaining--;
                throw new HttpRequestException("Temporary transport failure.");
            }
            HasMedia = CompleteMediaAfterTransfer;
            progress?.Report(new WidevineOfflineDownloadProgress(1024, 1024, 100));
            return Task.CompletedTask;
        }
    }

    private sealed class CaptureProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }
}
