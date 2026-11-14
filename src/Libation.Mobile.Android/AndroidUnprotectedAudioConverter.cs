using Android.App;
using Android.OS;
using AndroidX.Media3.Common;
using AndroidX.Media3.Transformer;
using Java.IO;
using Libation.Mobile.Core;

namespace Libation.Mobile.Android;

/// <summary>
/// Uses Android Media3 Transformer to make an AAC-in-MP4 (.m4b) copy of
/// an unprotected local audio file. This component deliberately rejects
/// Audible AAX/AAXC; it does not extract keys or remove provider DRM.
/// </summary>
public sealed class AndroidUnprotectedAudioConverter : IUnprotectedAudioConverter
{
    private readonly Android.Content.Context _context;
    private readonly SemaphoreSlim _exportGate = new(1, 1);

    public AndroidUnprotectedAudioConverter(Android.Content.Context? context = null)
        => _context = context?.ApplicationContext ?? Application.Context;

    public async Task ConvertToM4bAsync(
        string sourceFilePath,
        string destinationFilePath,
        CancellationToken cancellationToken = default)
    {
        LocalAudioConversionPolicy.EnsureSupportedUnprotectedInput(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        var source = Path.GetFullPath(sourceFilePath);
        var destination = Path.GetFullPath(destinationFilePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("The source audiobook audio was not found.", source);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The M4B output must be a different file from the source.", nameof(destinationFilePath));
        if (!string.Equals(Path.GetExtension(destination), ".m4b", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output filename must use the .m4b extension.", nameof(destinationFilePath));

        await _exportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            TryDelete(destination);

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var listener = new ExportListener(completion, destination);
            Transformer? transformer = null;
            var mainHandler = new Handler(Looper.MainLooper!);

            void StartOnMainThread()
            {
                try
                {
                    // Media3 Transformer only exports decoded media samples. It is
                    // not given Audible licence keys and cannot open protected AAXC.
                    transformer = new Transformer.Builder(_context)
                        .SetAudioMimeType("audio/mp4a-latm")
                        .AddListener(listener)
                        .Build();

                    var mediaItem = MediaItem.FromUri(
                        Android.Net.Uri.FromFile(new Java.IO.File(source)));
                    transformer.Start(mediaItem, destination);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(new InvalidOperationException(
                        "Android could not start the unprotected M4B export.", exception));
                }
            }

            if (Looper.MyLooper() == Looper.MainLooper)
                StartOnMainThread();
            else if (!mainHandler.Post(new Java.Lang.Runnable(StartOnMainThread)))
                throw new InvalidOperationException("Android's media thread is unavailable.");

            using var registration = cancellationToken.Register(() =>
            {
                mainHandler.Post(new Java.Lang.Runnable(() =>
                {
                    try { transformer?.Cancel(); }
                    catch (Exception) { /* Cancellation still completes below. */ }
                    completion.TrySetCanceled(cancellationToken);
                }));
            });

            try
            {
                await completion.Task.ConfigureAwait(false);
                VerifyMp4Output(destination);
            }
            catch
            {
                TryDelete(destination);
                throw;
            }
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private static void VerifyMp4Output(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length < 16)
            throw new InvalidDataException("The M4B export completed without a valid output file.");

        Span<byte> header = stackalloc byte[12];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Read(header) != header.Length ||
            !header.Slice(4, 4).SequenceEqual("ftyp"u8))
            throw new InvalidDataException("The M4B export is not a valid MP4-family audio container.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class ExportListener(
        TaskCompletionSource<bool> completion,
        string destination) : Java.Lang.Object, ITransformerListener
    {
        public void OnCompleted(Composition composition, ExportResult exportResult)
        {
            try
            {
                VerifyMp4Output(destination);
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        public void OnError(
            Composition composition,
            ExportResult exportResult,
            ExportException exportException)
        {
            // Do not surface native logs or file URLs in the UI.
            completion.TrySetException(new InvalidOperationException(
                "Android could not convert this unprotected audio file to M4B."));
        }

        public void OnFallbackApplied(
            Composition composition,
            TransformationRequest originalTransformationRequest,
            TransformationRequest fallbackTransformationRequest)
        {
            // The requested AAC output is explicitly configured. Any platform
            // fallback is still validated by the final MP4 signature check.
        }
    }
}
