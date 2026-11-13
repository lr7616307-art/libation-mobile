using System.Buffers.Binary;
using System.Xml;
using System.Xml.Linq;

namespace Libation.Mobile.Audible;

/// <summary>
/// The Widevine initialization data and audio MIME type parsed from a DASH MPD.
/// InitializationData is an opaque PSSH box for Android MediaDrm; this parser
/// does not decode or interpret Widevine licence messages or keys.
/// </summary>
public sealed record AudibleWidevineStreamKey(int PeriodIndex, int GroupIndex, int TrackIndex);

public sealed record AudibleWidevineManifestInfo(
    byte[] InitializationData,
    string MimeType,
    IReadOnlyList<AudibleWidevineStreamKey> AudioStreamKeys);

/// <summary>
/// Extracts the Widevine PSSH and audio MIME type from provider-supplied DASH XML.
/// XML entity resolution is disabled and document/message sizes are bounded.
/// </summary>
public static class AudibleWidevineManifestParser
{
    private const string WidevineSystemIdHex = "edef8ba979d64acea3c827dcd51d21ed";
    private const int MaximumManifestCharacters = 4 * 1024 * 1024;
    private const int MaximumPsshBytes = 1024 * 1024;
    private const string WidevineScheme = "urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed";

    public static AudibleWidevineManifestInfo Parse(string manifestXml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestXml);
        if (manifestXml.Length > MaximumManifestCharacters)
            throw new InvalidDataException("The Audible DASH manifest exceeds the supported size.");

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumManifestCharacters,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };
            using var stringReader = new StringReader(manifestXml);
            using var xmlReader = XmlReader.Create(stringReader, settings);
            document = XDocument.Load(xmlReader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("The Audible DASH manifest is not valid XML.", exception);
        }

        if (!string.Equals(document.Root?.Name.LocalName, "MPD", StringComparison.Ordinal))
            throw new InvalidDataException("The Audible manifest root element is not a DASH MPD.");

        var periods = document.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "Period", StringComparison.Ordinal))
            .ToArray();
        if (periods.Length == 0 || periods.Length > 128)
            throw new InvalidDataException("The Audible DASH manifest has an unsupported period count.");

        var streamKeys = new List<AudibleWidevineStreamKey>(periods.Length);
        var selectedMimeType = string.Empty;
        XElement? selectedWidevineProtection = null;

        for (var periodIndex = 0; periodIndex < periods.Length; periodIndex++)
        {
            var adaptationSets = periods[periodIndex].Elements()
                .Where(element => string.Equals(element.Name.LocalName, "AdaptationSet", StringComparison.Ordinal))
                .ToArray();

            // Audible audiobooks are audio-only. Refuse a manifest with video or
            // multiple adaptation groups rather than downloading the wrong track
            // or wasting the user's storage on unrelated representations.
            if (adaptationSets.Length != 1)
                throw new NotSupportedException(
                    "The Audible DASH manifest does not contain exactly one audio adaptation set per period.");

            var adaptationSet = adaptationSets[0];
            var mimeType = FindAudioMimeType(adaptationSet);
            if (string.IsNullOrEmpty(selectedMimeType))
                selectedMimeType = mimeType;
            else if (!string.Equals(selectedMimeType, mimeType, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    "The Audible DASH manifest changes audio MIME type between periods.");

            var representations = adaptationSet.Elements()
                .Where(element => string.Equals(element.Name.LocalName, "Representation", StringComparison.Ordinal))
                .ToArray();
            if (representations.Length == 0 || representations.Length > 128)
                throw new InvalidDataException(
                    "An Audible DASH audio adaptation set has no supported representations.");

            var selectedRepresentationIndex = SelectHighestBandwidthRepresentation(representations);
            streamKeys.Add(new AudibleWidevineStreamKey(periodIndex, 0, selectedRepresentationIndex));

            if (selectedWidevineProtection is null)
            {
                selectedWidevineProtection = adaptationSet.Elements().FirstOrDefault(element =>
                    string.Equals(element.Name.LocalName, "ContentProtection", StringComparison.Ordinal) &&
                    string.Equals(
                        (string?)element.Attribute("schemeIdUri"),
                        WidevineScheme,
                        StringComparison.OrdinalIgnoreCase));
            }
        }

        if (selectedWidevineProtection is null)
            throw new NotSupportedException(
                "The Audible DASH manifest does not contain a supported Widevine protection entry.");

        var psshElement = selectedWidevineProtection.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "pssh", StringComparison.Ordinal) &&
            string.Equals(element.Name.NamespaceName, "urn:mpeg:cenc:2013", StringComparison.Ordinal));

        if (psshElement is null || string.IsNullOrWhiteSpace(psshElement.Value))
            throw new NotSupportedException(
                "The Audible Widevine protection entry does not contain PSSH initialization data.");

        var initializationData = DecodeAndValidatePssh(psshElement.Value);
        return new AudibleWidevineManifestInfo(initializationData, selectedMimeType, streamKeys);
    }

    private static int SelectHighestBandwidthRepresentation(IReadOnlyList<XElement> representations)
    {
        var selectedIndex = 0;
        long highestBandwidth = -1;
        for (var index = 0; index < representations.Count; index++)
        {
            var bandwidthText = (string?)representations[index].Attribute("bandwidth");
            if (!long.TryParse(
                    bandwidthText,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var bandwidth) ||
                bandwidth < 0)
                bandwidth = 0;

            if (bandwidth > highestBandwidth)
            {
                highestBandwidth = bandwidth;
                selectedIndex = index;
            }
        }

        return selectedIndex;
    }

    private static byte[] DecodeAndValidatePssh(string encodedPssh)
    {
        byte[] pssh;
        try
        {
            pssh = Convert.FromBase64String(encodedPssh);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The Widevine PSSH value is not valid Base64.", exception);
        }

        if (pssh.Length is < 32 or > MaximumPsshBytes)
            throw new InvalidDataException("The Widevine PSSH has an unsupported size.");

        var declaredSize = BinaryPrimitives.ReadUInt32BigEndian(pssh.AsSpan(0, 4));
        if (declaredSize != pssh.Length ||
            pssh[4] != (byte)'p' || pssh[5] != (byte)'s' ||
            pssh[6] != (byte)'s' || pssh[7] != (byte)'h')
            throw new InvalidDataException("The Widevine initialization data is not a valid PSSH box.");

        var version = pssh[8];
        if (version is not 0 and not 1)
            throw new InvalidDataException("The Widevine PSSH version is not supported.");

        var systemId = Convert.ToHexString(pssh.AsSpan(12, 16)).ToLowerInvariant();
        if (!string.Equals(systemId, WidevineSystemIdHex, StringComparison.Ordinal))
            throw new InvalidDataException("The PSSH box does not identify the Widevine system.");

        var dataSizeOffset = 28;
        if (version == 1)
        {
            if (pssh.Length < 32)
                throw new InvalidDataException("The version 1 Widevine PSSH is truncated.");

            var keyCount = BinaryPrimitives.ReadUInt32BigEndian(pssh.AsSpan(28, 4));
            if (keyCount > (uint)((pssh.Length - 32) / 16))
                throw new InvalidDataException("The version 1 Widevine PSSH key list is truncated.");

            dataSizeOffset = checked(32 + (int)keyCount * 16);
        }

        if (dataSizeOffset > pssh.Length - 4)
            throw new InvalidDataException("The Widevine PSSH is truncated.");

        var dataSize = BinaryPrimitives.ReadUInt32BigEndian(pssh.AsSpan(dataSizeOffset, 4));
        if (dataSize != pssh.Length - dataSizeOffset - 4)
            throw new InvalidDataException("The Widevine PSSH data length is inconsistent.");

        return pssh;
    }

    private static string FindAudioMimeType(XElement adaptationSet)
    {
        var representationMimeType = adaptationSet
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "Representation", StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute("mimeType"))
            .FirstOrDefault(value => IsAudioMimeType(value));

        var adaptationMimeType = (string?)adaptationSet.Attribute("mimeType");
        var mimeType = representationMimeType ?? adaptationMimeType;

        if (!IsAudioMimeType(mimeType))
            throw new InvalidDataException(
                "The Widevine DASH AdaptationSet does not declare a supported audio MIME type.");

        return mimeType!.Trim();
    }

    private static bool IsAudioMimeType(string? mimeType)
        => !string.IsNullOrWhiteSpace(mimeType) &&
           mimeType.Trim().StartsWith("audio/", StringComparison.OrdinalIgnoreCase) &&
           mimeType.Length <= 128 &&
           !mimeType.Any(char.IsControl);
}
