using System.Net;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class OfflineDownloadRetryPolicyTests
{
    [Fact]
    public void IsRetryable_AcceptsTransportFailuresWithoutAnHttpStatus()
    {
        Assert.True(OfflineDownloadRetryPolicy.IsRetryable(new HttpRequestException("Connection reset.")));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void IsRetryable_AcceptsTransientHttpStatuses(HttpStatusCode statusCode)
    {
        var exception = new HttpRequestException("Temporary provider failure.", null, statusCode);
        Assert.True(OfflineDownloadRetryPolicy.IsRetryable(exception));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public void IsRetryable_RejectsPermanentHttpStatuses(HttpStatusCode statusCode)
    {
        var exception = new HttpRequestException("Provider rejected the request.", null, statusCode);
        Assert.False(OfflineDownloadRetryPolicy.IsRetryable(exception));
    }

    [Fact]
    public void IsRetryable_RejectsCancellationValidationAndUnsupportedMedia()
    {
        Assert.False(OfflineDownloadRetryPolicy.IsRetryable(new OperationCanceledException()));
        Assert.False(OfflineDownloadRetryPolicy.IsRetryable(new InvalidDataException()));
        Assert.False(OfflineDownloadRetryPolicy.IsRetryable(new UnauthorizedAccessException()));
        Assert.False(OfflineDownloadRetryPolicy.IsRetryable(new NotSupportedException()));
    }

    [Fact]
    public void IsRetryable_InspectsWrappedTransportFailures()
    {
        var wrapped = new InvalidOperationException(
            "Media transfer failed.",
            new HttpRequestException("Connection reset."));

        Assert.True(OfflineDownloadRetryPolicy.IsRetryable(wrapped));
    }

    [Fact]
    public void GetDelay_UsesBoundedExponentialBackoff()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), OfflineDownloadRetryPolicy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), OfflineDownloadRetryPolicy.GetDelay(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => OfflineDownloadRetryPolicy.GetDelay(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => OfflineDownloadRetryPolicy.GetDelay(3));
    }
}
