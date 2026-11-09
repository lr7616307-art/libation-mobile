using System.Text.Json;

namespace Libation.Mobile.Core;

/// <summary>Persists non-secret library synchronization metadata in app-private storage.</summary>
public sealed class JsonSyncStateStore(string filePath) : ISyncStateStore
{
    private readonly string _filePath = Path.GetFullPath(
        string.IsNullOrWhiteSpace(filePath)
            ? throw new ArgumentException("A state file path is required.", nameof(filePath))
            : filePath);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<DateTimeOffset?> GetLastSuccessfulSyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath)) return null;
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var state = await JsonSerializer.DeserializeAsync<SyncState>(stream, cancellationToken: cancellationToken);
            return state?.LastSuccessfulSync;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveLastSuccessfulSyncAsync(DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException("The sync-state path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                {
                    await JsonSerializer.SerializeAsync(stream, new SyncState(timestamp), cancellationToken: cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                File.Move(temporaryPath, _filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally { _gate.Release(); }
    }

    private sealed record SyncState(DateTimeOffset LastSuccessfulSync);
}
