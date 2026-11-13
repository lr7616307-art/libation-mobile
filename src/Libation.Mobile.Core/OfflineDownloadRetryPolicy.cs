namespace Libation.Mobile.Core;

/// <summary>
/// Conservative retry rules for provider-authorized offline media transfers.
/// Only transient transport failures are retried automatically; provider
/// denials, unsupported formats, validation failures and cancellation are not.
/// </summary>
public static class OfflineDownloadRetryPolicy
{
    public const int MaximumAutomaticRetries = 2;

    public static bool IsRetryable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException
            or InvalidDataException
            or UnauthorizedAccessException
            or NotSupportedException)
            return false;

        if (exception is HttpRequestException requestException)
        {
            var statusCode = (int?)requestException.StatusCode;
            return statusCode is null
                || statusCode == 408
                || statusCode == 429
                || statusCode >= 500;
        }

        if (exception is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(IsRetryable);

        return exception.InnerException is not null
            && IsRetryable(exception.InnerException);
    }

    /// <summary>
    /// Exponential delays for retry numbers 1..MaximumAutomaticRetries.
    /// </summary>
    public static TimeSpan GetDelay(int retryNumber)
    {
        if (retryNumber < 1 || retryNumber > MaximumAutomaticRetries)
            throw new ArgumentOutOfRangeException(nameof(retryNumber));

        return TimeSpan.FromSeconds(1 << (retryNumber - 1));
    }
}
