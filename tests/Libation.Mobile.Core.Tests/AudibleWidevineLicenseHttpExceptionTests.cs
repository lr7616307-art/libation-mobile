using System.Net;
using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleWidevineLicenseHttpExceptionTests
{
    [Theory]
    [InlineData("{\"reason\":\"invalid_challenge\"}", "InvalidChallenge")]
    [InlineData("{\"reason\":\"device_not_registered\"}", "DeviceRegistration")]
    [InlineData("{\"reason\":\"request throttled\"}", "RateLimited")]
    [InlineData("{\"reason\":\"authorization failed\"}", "Authentication")]
    [InlineData("{\"reason\":\"title entitlement denied\"}", "TitleEligibility")]
    [InlineData("{\"reason\":\"marketplace restricted\"}", "MarketplaceRestriction")]
    [InlineData("{\"reason\":\"unrecognized private detail\"}", "Unclassified")]
    [InlineData("not-json", "Unclassified")]
    public void FromResponse_MapsProviderReasonToSafeFixedCategory(string body, string expected)
    {
        var exception = AudibleWidevineLicenseHttpException.FromResponse(
            HttpStatusCode.BadRequest, body);

        Assert.Equal(expected, exception.FailureCode);
        Assert.Contains("HTTP 400", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private detail", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromResponse_DoesNotRetainRawProviderMessageOrAccountIdentifiers()
    {
        const string privateValue = "customer-A12345678901-title-B012345678";
        var exception = AudibleWidevineLicenseHttpException.FromResponse(
            HttpStatusCode.BadRequest,
            "{\"reason\":\"invalid challenge for " + privateValue + "\"}");

        Assert.Equal("InvalidChallenge", exception.FailureCode);
        Assert.DoesNotContain(privateValue, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(privateValue, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FromResponse_RejectsOversizedAndNonStringReasonValues()
    {
        var oversized = AudibleWidevineLicenseHttpException.FromResponse(
            HttpStatusCode.BadRequest,
            new string('x', 16 * 1024 + 1));
        var nonString = AudibleWidevineLicenseHttpException.FromResponse(
            HttpStatusCode.BadRequest,
            "{\"reason\":{\"private\":\"value\"}}");

        Assert.Equal("Unclassified", oversized.FailureCode);
        Assert.Equal("Unclassified", nonString.FailureCode);
    }
}
