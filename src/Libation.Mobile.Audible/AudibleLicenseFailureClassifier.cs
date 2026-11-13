using AudibleApi;
using AudibleApi.Common;

namespace Libation.Mobile.Audible;

/// <summary>
/// A provider-issued title/device response that is not Widevine. VØRATH does not
/// silently switch to a different protected format.
/// </summary>
public sealed class AudibleWidevineNotAvailableException : NotSupportedException
{
    public AudibleWidevineNotAvailableException()
        : base("Audible returned a non-Widevine content licence for this title/device.") { }
}

/// <summary>
/// Converts Audible's typed licence-denial categories into short user-facing
/// diagnostics. Provider messages, ASINs, customer identifiers and response
/// bodies are deliberately never copied into the UI or Android notifications.
/// </summary>
/// <summary>
/// A non-granted Audible Widevine content response. Only bounded status and
/// DRM-category codes are retained; no title, account, URL or response body.
/// </summary>
public sealed class AudibleWidevineContentGrantException : InvalidOperationException
{
    public string StatusCode { get; }
    public string DrmType { get; }

    public AudibleWidevineContentGrantException(string statusCode, string drmType)
        : base("Audible did not grant a Widevine content licence for this title/device.")
    {
        StatusCode = SafeCode(statusCode, "Granted", "Missing", "Denied", "Rejected", "Unavailable", "Other");
        DrmType = SafeCode(drmType, "Widevine", "Adrm", "Mpeg", "Unknown", "Other");
    }

    private static string SafeCode(string? value, params string[] allowed)
        => allowed.FirstOrDefault(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
            ?? "Other";
}

public static class AudibleLicenseFailureClassifier
{
    public static string? GetSafeUserMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AudibleWidevineContentGrantException grant)
            {
                return $"Audible content grant was not successful (status: {grant.StatusCode}; DRM: {grant.DrmType}). " +
                       "This is the provider's title/device response, not a download-transfer failure. " +
                       "VØRATH did not retry or switch to another protected format.";
            }

            if (current is AudibleAndroidRegistrationRequiredException)
            {
                return "This Audible account was saved with an older or mismatched device registration. " +
                       "Open Accounts and choose Refresh Android registration, then sign in once. " +
                       "This is a one-time account registration refresh, not approval for each audiobook.";
            }

            if (current is AudibleWidevineNotAvailableException)
            {
                return "Audible returned a content format other than Widevine for this title/device. " +
                       "The title may not offer Widevine, or this account's saved device registration may not be eligible. " +
                       "VØRATH did not switch to another protected format.";
            }

            if (current is ContentLicenseDeniedException denied)
                return DescribeDenial(denied);
        }

        return null;
    }

    private static string DescribeDenial(ContentLicenseDeniedException denied)
    {
        var checks = new[]
        {
            (Name: "Client", Reason: denied.Client),
            (Name: "Ownership", Reason: denied.Ownership),
            (Name: "Membership", Reason: denied.Membership),
            (Name: "AYCL", Reason: denied.AYCL)
        };

        if (denied.IsCustomerThrottled ||
            checks.Any(check => string.Equals(
                check.Reason?.RejectionReason,
                RejectionReason.CustomerThrottled,
                StringComparison.OrdinalIgnoreCase)))
        {
            return "Audible is temporarily limiting offline-licence requests for this account (CustomerThrottled). " +
                   "Do not keep pressing Retry; wait before making another attempt.";
        }

        var requesterRejected = checks.Any(check =>
            string.Equals(check.Name, "Client", StringComparison.Ordinal) &&
            string.Equals(check.Reason?.RejectionReason, RejectionReason.RequesterEligibility,
                StringComparison.OrdinalIgnoreCase));

        if (requesterRejected)
        {
            return "Audible rejected this device/client for offline licensing (Client / RequesterEligibility). " +
                   "Check that the account is registered in VØRATH with the current Android device profile.";
        }

        var ownershipRejected = checks.Any(check =>
            string.Equals(check.Name, "Ownership", StringComparison.Ordinal) &&
            string.Equals(check.Reason?.RejectionReason, RejectionReason.ContentEligibility,
                StringComparison.OrdinalIgnoreCase));

        var membershipRejected = checks.Any(check =>
            string.Equals(check.Name, "Membership", StringComparison.Ordinal) &&
            string.Equals(check.Reason?.RejectionReason, RejectionReason.ContentEligibility,
                StringComparison.OrdinalIgnoreCase));

        var ayclRejected = checks.Any(check =>
            string.Equals(check.Name, "AYCL", StringComparison.Ordinal) &&
            string.Equals(check.Reason?.RejectionReason, RejectionReason.ContentEligibility,
                StringComparison.OrdinalIgnoreCase));

        if (ownershipRejected && membershipRejected)
        {
            return "Audible did not confirm either ownership or membership eligibility for this title. " +
                   "Check the signed-in account, marketplace and title entitlement.";
        }

        if (ownershipRejected)
        {
            return "Audible's ownership check did not confirm this title for offline download. " +
                   "Check that the signed-in account and marketplace own the title.";
        }

        if (membershipRejected)
        {
            return "Audible's membership check did not confirm this title for offline download. " +
                   "Check the membership entitlement and the title's availability in that marketplace.";
        }

        if (ayclRejected)
        {
            return "Audible's title-eligibility check refused the offline licence (AYCL / ContentEligibility). " +
                   "Check the title's download eligibility for this account.";
        }

        var categories = checks
            .Where(check => !string.IsNullOrWhiteSpace(check.Reason?.RejectionReason))
            .Select(check => $"{check.Name} / {SafeReasonCode(check.Reason!.RejectionReason!)}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return categories.Length > 0
            ? $"Audible denied the offline licence at: {string.Join(", ", categories)}. " +
              "Check this title's entitlement and the account/device's eligibility for offline playback."
            : "Audible denied the offline licence for this title/device. " +
              "Check the title's entitlement and the account/device's eligibility for offline playback.";
    }

    private static string SafeReasonCode(string value)
        => value is RejectionReason.ContentEligibility
            or RejectionReason.RequesterEligibility
            or RejectionReason.GenericError
            or RejectionReason.CustomerThrottled
                ? value
                : "Other";
}
