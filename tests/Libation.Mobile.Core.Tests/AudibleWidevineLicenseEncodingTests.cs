using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleWidevineLicenseEncodingTests
{
    [Fact]
    public void EncodeChallenge_UsesStandardBase64WithoutChangingBytes()
    {
        byte[] challenge = [0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF];

        var encoded = AudibleWidevineLicenseEncoding.EncodeChallenge(challenge);

        Assert.Equal(Convert.ToBase64String(challenge), encoded);
        Assert.Equal(challenge, Convert.FromBase64String(encoded));
    }

    [Fact]
    public void DecodeLicenseResponse_ReturnsOpaqueProviderBytes()
    {
        byte[] response = [0x10, 0x20, 0x30, 0x80, 0xFE];
        var encoded = Convert.ToBase64String(response);

        var decoded = AudibleWidevineLicenseEncoding.DecodeLicenseResponse(encoded);

        Assert.Equal(response, decoded);
    }

    [Fact]
    public void DecodeLicenseResponse_RejectsWhitespaceAndMalformedInput()
    {
        Assert.Throws<InvalidDataException>(() =>
            AudibleWidevineLicenseEncoding.DecodeLicenseResponse("AA A="));
        Assert.Throws<InvalidDataException>(() =>
            AudibleWidevineLicenseEncoding.DecodeLicenseResponse("not-base64"));
    }

    [Fact]
    public void EncodeChallenge_RejectsEmptyAndOversizedInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AudibleWidevineLicenseEncoding.EncodeChallenge([]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AudibleWidevineLicenseEncoding.EncodeChallenge(new byte[AudibleWidevineLicenseEncoding.MaximumMessageBytes + 1]));
    }

    [Fact]
    public void DecodeLicenseResponse_RejectsOversizedInput()
    {
        var oversized = Convert.ToBase64String(
            new byte[AudibleWidevineLicenseEncoding.MaximumMessageBytes + 1]);

        Assert.Throws<InvalidDataException>(() =>
            AudibleWidevineLicenseEncoding.DecodeLicenseResponse(oversized));
    }
}
