namespace Libation.Mobile.Core;

public interface IDownloadQueue
{
    Task EnqueueAsync(string audiobookId, CancellationToken cancellationToken = default);
}
