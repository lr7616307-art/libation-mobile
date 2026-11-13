using AudibleApi;
using AudibleApi.Common;
using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleLicenseFailureClassifierTests
{
    [Fact]
    public void GetSafeUserMessage_ExplainsTemporaryCustomerThrottle()
    {
        var exception = CreateDenial(("AYCL", RejectionReason.CustomerThrottled));

        var message = AudibleLicenseFailureClassifier.GetSafeUserMessage(exception);

        Assert.NotNull(message);
        Assert.Contains("temporarily limiting", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CustomerThrottled", message, StringComparison.Ordinal);
        Assert.Contains("Do not keep pressing Retry", message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSafeUserMessage_ExplainsDeviceRegistrationRejection()
    {
        var exception = CreateDenial(("Client", RejectionReason.RequesterEligibility));

        var message = AudibleLicenseFailureClassifier.GetSafeUserMessage(exception);

        Assert.NotNull(message);
        Assert.Contains("device/client", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("current Android device profile", message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSafeUserMessage_ExplainsOwnershipRejection()
    {
        var exception = CreateDenial(("Ownership", RejectionReason.ContentEligibility));

        var message = AudibleLicenseFailureClassifier.GetSafeUserMessage(exception);

        Assert.NotNull(message);
        Assert.Contains("ownership check", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("signed-in account and marketplace", message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSafeUserMessage_ExplainsMembershipRejection()
    {
        var exception = CreateDenial(("Membership", RejectionReason.ContentEligibility));

        var message = AudibleLicenseFailureClassifier.GetSafeUserMessage(exception);

        Assert.NotNull(message);
        Assert.Contains("membership check", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("membership entitlement", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSafeUserMessage_DoesNotExposeProviderMessageOrCustomerIdentifier()
    {
        const string privateCustomerId = "A12345678901";
        var license = new ContentLicense
        {
            Asin = "B012345678",
            LicenseDenialReasons =
            [
                new LicenseDenialReason
                {
                    ValidationType = "Membership",
                    RejectionReason = RejectionReason.ContentEligibility,
                    Message = $"Private response for [{privateCustomerId}]"
                }
            ]
        };
        var exception = new ContentLicenseDeniedException(null, license);

        var message = AudibleLicenseFailureClassifier.GetSafeUserMessage(exception);

        Assert.NotNull(message);
        Assert.DoesNotContain(privateCustomerId, message, StringComparison.Ordinal);
        Assert.DoesNotContain("Private response", message, StringComparison.Ordinal);
        Assert.DoesNotContain("B012345678", message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSafeUserMessage_FindsDenialWrappedByAnotherException()
    {
        var denial = CreateDenial(("Ownership", RejectionReason.ContentEligibility));
        var wrapped = new InvalidOperationException("Download failed.", denial);

        var message = AudibleLicenseFailureClassifier.GetSafeUserMessage(wrapped);

        Assert.NotNull(message);
        Assert.Contains("ownership check", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSafeUserMessage_ExplainsOneTimeAndroidRegistrationRefresh()
    {
        var message = AudibleLicenseFailureClassifier.GetSafeUserMessage(
            new AudibleAndroidRegistrationRequiredException());

        Assert.NotNull(message);
        Assert.Contains("Refresh Android registration", message, StringComparison.Ordinal);
        Assert.Contains("one-time account registration refresh", message, StringComparison.Ordinal);
        Assert.Contains("not approval for each audiobook", message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSafeUserMessage_IdentifiesNonWidevineContentWithoutFallback()
    {
        var message = AudibleLicenseFailureClassifier.GetSafeUserMessage(
            new AudibleWidevineNotAvailableException());

        Assert.NotNull(message);
        Assert.Contains("other than Widevine", message, StringComparison.Ordinal);
        Assert.Contains("did not switch", message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSafeUserMessage_ReturnsNullForUnrelatedExceptions()
    {
        Assert.Null(AudibleLicenseFailureClassifier.GetSafeUserMessage(
            new InvalidOperationException("Network unavailable.")));
    }


    [Fact]
    public void GetSafeUserMessage_ReportsSafeWidevineGrantStatusAndDrmCategory()
    {
        var exception = new AudibleWidevineContentGrantException("Denied", "Adrm");

        var message = AudibleLicenseFailureClassifier.GetSafeUserMessage(exception);

        Assert.NotNull(message);
        Assert.Contains("status: Denied", message, StringComparison.Ordinal);
        Assert.Contains("DRM: Adrm", message, StringComparison.Ordinal);
        Assert.Contains("not a download-transfer failure", message, StringComparison.Ordinal);
    }

    [Fact]
    public void WidevineGrantException_DoesNotRetainUnrecognizedProviderText()
    {
        var exception = new AudibleWidevineContentGrantException(
            "Denied: private customer details", "Widevine?token=secret");

        Assert.Equal("Other", exception.StatusCode);
        Assert.Equal("Other", exception.DrmType);
        Assert.DoesNotContain("private customer", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ContentLicenseDeniedException CreateDenial(params (string Validation, string Reason)[] reasons)
    {
        var license = new ContentLicense
        {
            Asin = "B012345678",
            LicenseDenialReasons = reasons.Select(reason => new LicenseDenialReason
            {
                ValidationType = reason.Validation,
                RejectionReason = reason.Reason,
                Message = "Provider-specific message that must not be shown."
            }).ToArray()
        };

        return new ContentLicenseDeniedException(null, license);
    }
}
