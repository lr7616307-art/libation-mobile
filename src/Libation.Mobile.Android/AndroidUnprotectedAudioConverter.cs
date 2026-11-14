using Android.OS;
using Android.Runtime;
using Libation.Mobile.Core;
using System.Runtime.InteropServices;

namespace Libation.Mobile.Android;

/// <summary>
/// Exports unprotected local audio through Android Media3 Transformer.
/// The Java bridge handles Transformer.Listener on Android's main looper.
/// Protected Audible AAX/AAXC is rejected before native code is invoked.
/// </summary>
public sealed class AndroidUnprotectedAudioConverter : IUnprotectedAudioConverter
{
    private const string BridgeClassName = "org/libationmobile/app/UnprotectedM4bExporter";
    private const string ExportMethodSignature =
        "(Landroid/content/Context;Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;";

    private readonly global::Android.Content.Context _context;
    private readonly SemaphoreSlim _exportGate = new(1, 1);

    public AndroidUnprotectedAudioConverter(global::Android.Content.Context? context = null)
        => _context = context?.ApplicationContext ?? global::Android.App.Application.Context;

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

            // Copy an existing M4B byte-for-byte; transcoding it again would be lossy.
            if (string.Equals(Path.GetExtension(source), ".m4b", StringComparison.OrdinalIgnoreCase))
            {
                VerifyMp4Output(source);
                File.Copy(source, destination, overwrite: false);
                VerifyMp4Output(destination);
                return;
            }

            // Resolve the app Java class while on the calling (UI) thread, then
            // promote it to a global reference before executing on a worker thread.
            var localClass = JNIEnv.FindClass(BridgeClassName);
            if (localClass == IntPtr.Zero)
                throw new InvalidOperationException("The Android M4B export bridge was not packaged.");

            var globalClass = JNIEnv.NewGlobalRef(localClass);
            var method = JNIEnv.GetStaticMethodID(globalClass, "export", ExportMethodSignature);
            JNIEnv.DeleteLocalRef(localClass);
            if (method == IntPtr.Zero)
            {
                JNIEnv.DeleteGlobalRef(globalClass);
                throw new InvalidOperationException("The Android M4B export bridge method is unavailable.");
            }

            try
            {
                // Media3 runs on Android's main looper and reports completion through
                // the Java bridge. Do not block the UI while it encodes the file.
                var safeError = await Task.Run(
                    () => InvokeJavaExporter(globalClass, method, source, destination),
                    CancellationToken.None).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(safeError))
                    throw new InvalidOperationException(safeError);

                VerifyMp4Output(destination);
            }
            catch
            {
                TryDelete(destination);
                throw;
            }
            finally
            {
                JNIEnv.DeleteGlobalRef(globalClass);
            }
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private string? InvokeJavaExporter(IntPtr globalClass, IntPtr method, string source, string destination)
    {
        IntPtr sourceString = IntPtr.Zero;
        IntPtr destinationString = IntPtr.Zero;
        IntPtr result = IntPtr.Zero;
        try
        {
            sourceString = JNIEnv.NewString(source);
            destinationString = JNIEnv.NewString(destination);
            var arguments = new[]
            {
                new JValue(_context.Handle),
                new JValue(sourceString),
                new JValue(destinationString)
            };
            result = JNIEnv.CallStaticObjectMethod(globalClass, method, arguments);
            return result == IntPtr.Zero
                ? null
                : JNIEnv.GetString(result, JniHandleOwnership.DoNotTransfer);
        }
        finally
        {
            if (result != IntPtr.Zero) JNIEnv.DeleteLocalRef(result);
            if (sourceString != IntPtr.Zero) JNIEnv.DeleteLocalRef(sourceString);
            if (destinationString != IntPtr.Zero) JNIEnv.DeleteLocalRef(destinationString);
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
}
