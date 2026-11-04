using Android.Media;
using Libation.Mobile.Core;

namespace Libation.Mobile.Android;

/// <summary>Android MediaPlayer adapter for imported local audio files.</summary>
public sealed class AndroidLocalAudioPlayer : ILocalAudioPlayer
{
    private readonly MediaPlayer _player = new();
    private bool _prepared;
    private bool _disposed;

    public bool IsPlaying => !_disposed && _prepared && _player.IsPlaying;
    public long PositionMilliseconds => !_disposed && _prepared ? Math.Max(0, _player.CurrentPosition) : 0;
    public long DurationMilliseconds => !_disposed && _prepared ? Math.Max(0, _player.Duration) : 0;

    public void Play(string filePath, long startPositionMilliseconds = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("Imported audiobook audio was not found.", filePath);

        _player.Reset();
        _prepared = false;
        _player.SetDataSource(filePath);
        _player.Prepare();
        _prepared = true;

        if (startPositionMilliseconds > 0)
            _player.SeekTo((int)Math.Clamp(startPositionMilliseconds, 0, int.MaxValue));
        _player.Start();
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_prepared && _player.IsPlaying)
            _player.Pause();
    }

    public void Seek(long positionMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_prepared) return;
        _player.SeekTo((int)Math.Clamp(positionMilliseconds, 0, int.MaxValue));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.Release();
        _player.Dispose();
    }
}
