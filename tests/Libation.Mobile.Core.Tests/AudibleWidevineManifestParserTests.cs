using System.Buffers.Binary;
using System.Text;
using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleWidevineManifestParserTests
{
    private const string WidevineScheme = "urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed";
    private const string CencNamespace = "urn:mpeg:cenc:2013";

    [Fact]
    public void Parse_ExtractsWidevinePsshAndAudioMimeType()
    {
        var pssh = CreatePssh(version: 0, [0x01, 0x02, 0x03, 0x04]);
        var xml = CreateManifest(Convert.ToBase64String(pssh), "audio/mp4");

        var result = AudibleWidevineManifestParser.Parse(xml);

        Assert.Equal(pssh, result.InitializationData);
        Assert.Equal("audio/mp4", result.MimeType);
        Assert.Equal([new AudibleWidevineStreamKey(0, 0, 0)], result.AudioStreamKeys);
    }

    [Fact]
    public void Parse_SelectsHighestBandwidthAudioRepresentation()
    {
        var pssh = CreatePssh(version: 0, [0x01, 0x02]);
        var xml = CreateManifest(Convert.ToBase64String(pssh), "audio/mp4")
            .Replace(
                "<Representation mimeType=\"audio/mp4\" />",
                "<Representation mimeType=\"audio/mp4\" bandwidth=\"64000\" />" +
                "<Representation mimeType=\"audio/mp4\" bandwidth=\"128000\" />",
                StringComparison.Ordinal);

        var result = AudibleWidevineManifestParser.Parse(xml);

        Assert.Equal([new AudibleWidevineStreamKey(0, 0, 1)], result.AudioStreamKeys);
    }

    [Fact]
    public void Parse_IgnoresNonAudioAdaptationSetsAndPreservesOriginalGroupIndex()
    {
        var pssh = CreatePssh(version: 0, [0x01]);
        var xml = CreateManifest(Convert.ToBase64String(pssh), "audio/mp4")
            .Replace(
                "<Period>",
                "<Period><AdaptationSet mimeType=\"application/ttml+xml\" contentType=\"text\"><Representation mimeType=\"application/ttml+xml\" /></AdaptationSet>",
                StringComparison.Ordinal)
            .Replace(
                "</Period>",
                "<AdaptationSet mimeType=\"video/mp4\"><Representation mimeType=\"video/mp4\" /></AdaptationSet></Period>",
                StringComparison.Ordinal);

        var result = AudibleWidevineManifestParser.Parse(xml);

        Assert.Equal([new AudibleWidevineStreamKey(0, 1, 0)], result.AudioStreamKeys);
        Assert.Equal("audio/mp4", result.MimeType);
    }
    [Fact]
    public void Parse_PrefersAudioRepresentationMimeType()
    {
        var pssh = CreatePssh(version: 0, [0x01]);
        var xml = CreateManifest(Convert.ToBase64String(pssh), "application/mp4")
            .Replace("<Representation mimeType=\"application/mp4\" />",
                "<Representation mimeType=\"audio/mp4\" />", StringComparison.Ordinal);

        var result = AudibleWidevineManifestParser.Parse(xml);

        Assert.Equal("audio/mp4", result.MimeType);
    }

    [Fact]
    public void Parse_RejectsManifestWithoutWidevineProtection()
    {
        var xml = CreateManifest(Convert.ToBase64String(CreatePssh(0, [0x01])), "audio/mp4")
            .Replace(WidevineScheme, "urn:uuid:00000000-0000-0000-0000-000000000000", StringComparison.Ordinal);

        Assert.Throws<NotSupportedException>(() => AudibleWidevineManifestParser.Parse(xml));
    }

    [Fact]
    public void Parse_RejectsMalformedPsshAndWrongSystemId()
    {
        var malformed = Convert.ToBase64String(Encoding.UTF8.GetBytes("not-a-pssh-box"));
        Assert.Throws<InvalidDataException>(() =>
            AudibleWidevineManifestParser.Parse(CreateManifest(malformed, "audio/mp4")));

        var wrongSystemId = CreatePssh(0, [0x01]);
        wrongSystemId[12] = 0x00;
        Assert.Throws<InvalidDataException>(() =>
            AudibleWidevineManifestParser.Parse(
                CreateManifest(Convert.ToBase64String(wrongSystemId), "audio/mp4")));
    }

    [Fact]
    public void Parse_RejectsNonAudioAdaptationSet()
    {
        var xml = CreateManifest(
            Convert.ToBase64String(CreatePssh(0, [0x01])), "video/mp4");

        Assert.Throws<InvalidDataException>(() => AudibleWidevineManifestParser.Parse(xml));
    }

    [Fact]
    public void Parse_RejectsDtdAndExternalEntities()
    {
        const string xml = """
            <!DOCTYPE MPD [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <MPD><Period><AdaptationSet mimeType="audio/mp4">
            <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed">
            <cenc:pssh xmlns:cenc="urn:mpeg:cenc:2013">&xxe;</cenc:pssh>
            </ContentProtection></AdaptationSet></Period></MPD>
            """;

        Assert.Throws<InvalidDataException>(() => AudibleWidevineManifestParser.Parse(xml));
    }

    private static string CreateManifest(string base64Pssh, string mimeType)
        => $"""
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" xmlns:cenc="{CencNamespace}">
              <Period>
                <AdaptationSet mimeType="{mimeType}">
                  <ContentProtection schemeIdUri="{WidevineScheme}">
                    <cenc:pssh>{base64Pssh}</cenc:pssh>
                  </ContentProtection>
                  <Representation mimeType="{mimeType}" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

    private static byte[] CreatePssh(byte version, byte[] data)
    {
        var pssh = new byte[32 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(pssh.AsSpan(0, 4), (uint)pssh.Length);
        pssh[4] = (byte)'p';
        pssh[5] = (byte)'s';
        pssh[6] = (byte)'s';
        pssh[7] = (byte)'h';
        pssh[8] = version;
        var systemId = Convert.FromHexString("edef8ba979d64acea3c827dcd51d21ed");
        systemId.CopyTo(pssh.AsSpan(12, 16));
        BinaryPrimitives.WriteUInt32BigEndian(pssh.AsSpan(28, 4), (uint)data.Length);
        data.CopyTo(pssh.AsSpan(32));
        return pssh;
    }
}
