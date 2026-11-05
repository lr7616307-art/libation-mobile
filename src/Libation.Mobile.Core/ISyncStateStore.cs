namespace Libation.Mobile.Core;

public interface ISyncStateStore
{
    Task<DateTimeOffset?> GetLastSuccessfulSyncAsync(CancellationToken cancellationToken = default);
    Task SaveLastSuccessfulSyncAsync(DateTimeOffset timestamp, CancellationToken cancellationToken = default);
}
