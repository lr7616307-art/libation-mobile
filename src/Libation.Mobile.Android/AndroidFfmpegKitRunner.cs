using Ffmpegkit.Droid;
using Libation.Mobile.Audible;

namespace Libation.Mobile.Android;

/// <summary>
/// Converts a fully downloaded Audible AAX/AAXC file using the voucher supplied
/// by Audible's ADRM licence. This is the same FFmpegKit argument path used by
/// LibriSync; no Widevine challenge, guessed key or DRM bypass is involved.
/// </summary>
public sealed class AndroidFfmpegKitRunner
{
    public async Task<string> ConvertToM4bAsync(
        string encryptedFilePath,
        string outputFilePath,
        AudibleAdrmDownloadPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        var input = Path.GetFullPath(encryptedFilePath);
        var output = Path.GetFullPath(outputFilePath);

        if (!File.Exists(input) || new FileInfo(input).Length <= 0)
            throw new FileNotFoundException("Audible's downloaded source file is missing or empty.", input);

        if (plan.MediaKind == AudibleAdrmMediaKind.Mpeg)
            throw new InvalidOperationException("An unprotected MPEG download does not need FFmpeg conversion.");

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporaryOutput = output + ".converting";
        TryDelete(temporaryOutput);

        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y" };

        switch (plan.MediaKind)
        {
            case AudibleAdrmMediaKind.Aaxc:
                if (!IsHexKey(plan.AudibleKeyHex, 32) || !IsHexKey(plan.AudibleIvHex, 32))
                    throw new InvalidDataException("Audible's AAXC voucher key or IV is invalid.");
                arguments.AddRange(["-audible_key", plan.AudibleKeyHex!, "-audible_iv", plan.AudibleIvHex!]);
                break;

            case AudibleAdrmMediaKind.Aax:
                if (!IsHexKey(plan.ActivationBytes, 8))
                    throw new InvalidDataException("Audible's AAX activation bytes are invalid.");
                arguments.AddRange(["-activation_bytes", plan.ActivationBytes!]);
                break;

            default:
                throw new NotSupportedException("This Audible media type cannot be converted.");
        }

        arguments.AddRange(
        [
            "-f", plan.MediaKind == AudibleAdrmMediaKind.Aaxc ? "aaxc" : "aax",
            "-i", input,
            "-map", "0:a:0",
            "-map_metadata", "0",
            "-map_chapters", "0",
            "-c:a", "copy",
            "-movflags", "+faststart",
            "-f", "ipod",
            temporaryOutput
        ]);

        cancellationToken.ThrowIfCancellationRequested();

        // FFmpegKit's synchronous entry point is deliberately moved to a worker
        // thread; conversion must never block Avalonia or Android's main looper.
        var result = await Task.Run(() => FFmpegKit.ExecuteWithArguments(arguments.ToArray()))
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (result?.ReturnCode?.IsValueSuccess != true)
        {
            var details = result?.FailStackTrace;
            if (string.IsNullOrWhiteSpace(details))
                details = result?.AllLogsAsString;
            if (!string.IsNullOrWhiteSpace(details) && details.Length > 1200)
                details = details[^1200..];

            TryDelete(temporaryOutput);
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(details)
                    ? "FFmpeg could not convert Audible's downloaded file into a playable M4B."
                    : "FFmpeg could not convert Audible's downloaded file into a playable M4B: " + details);
        }

        if (!File.Exists(temporaryOutput) || new FileInfo(temporaryOutput).Length <= 0)
        {
            TryDelete(temporaryOutput);
            throw new InvalidDataException("FFmpeg reported success but did not create a playable audiobook.");
        }

        File.Move(temporaryOutput, output, overwrite: true);
        return output;
    }

    private static bool IsHexKey(string? value, int expectedLength)
        => value is { Length: var length } &&
           length == expectedLength &&
           value.All(Uri.IsHexDigit);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
