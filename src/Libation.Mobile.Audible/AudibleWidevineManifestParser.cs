using System.Buffers.Binary;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Libation.Mobile.Audible;

/// <summary>
/// The Widevine initialization data and audio MIME type parsed from a DASH MPD.
/// InitializationData is an opaque PSSH box for Android MediaDrm; this parser
/// does not decode or interpret Widevine licence messages or keys.
/// </summary>
public sealed record AudibleWidevineManifestInfo(byte[] InitializationData, string MimeType);

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

        var widevineProtection = document
            .Descendants()
            .Where(element =>
                string.Equals(element.Name.LocalName, "ContentProtection", StringComparison.Ordinal) &&
                string.Equals(
                    (string?)element.Attribute("schemeIdUri"),
                    WidevineScheme,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var protection in widevineProtection)
        {
            var psshElement = protection.Elements().FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "pssh", StringComparison.Ordinal) &&
                string.Equals(element.Name.NamespaceName, "urn:mpeg:cenc:2013", StringComparison.Ordinal));

            if (psshElement is null || string.IsNullOrWhiteSpace(psshElement.Value))
                continue;

            var initializationData = DecodeAndValidatePssh(psshElement.Value);
            var mimeType = FindAudioMimeType(protection);
            return new AudibleWidevineManifestInfo(initializationData, mimeType);
        }

        throw new NotSupportedException(
            "The Audible DASH manifest does not contain a supported Widevine PSSH entry.");
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

    private static string FindAudioMimeType(XElement protection)
    {
        var adaptationSet = protection.Ancestors().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "AdaptationSet", StringComparison.Ordinal));
        if (adaptationSet is null)
            throw new InvalidDataException("The Widevine PSSH is not inside a DASH AdaptationSet.");

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
