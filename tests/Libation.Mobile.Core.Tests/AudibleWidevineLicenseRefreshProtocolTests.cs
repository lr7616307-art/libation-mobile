using Libation.Mobile.Audible;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleWidevineLicenseRefreshProtocolTests
{
    [Fact]
    public void CreateRequestBody_UsesDocumentedWidevineFieldsAndBase64Challenge()
    {
        byte[] challenge = [0x00, 0x01, 0x7F, 0x80, 0xFE];
        var reference = new AudibleWidevineProviderLicenseReference(
            "CR!content-reference", 17);

        var body = AudibleWidevineLicenseRefreshProtocol.CreateRequestBody(
            "B012345678", reference, challenge);

        Assert.Equal("Audible", body["tenant_id"]?.Value<string>());
        var requests = Assert.IsType<JArray>(body["license_refresh_requests"]);
        var request = Assert.IsType<JObject>(Assert.Single(requests));

        Assert.Equal("B012345678", request["asin"]?.Value<string>());
        Assert.Equal("CR!content-reference", request["acr"]?.Value<string>());
        Assert.Equal("17", request["file_version"]?.Value<string>());
        Assert.Equal("Widevine", request["drm_type"]?.Value<string>());
        Assert.Equal(Convert.ToBase64String(challenge), request["drm_challenge"]?.Value<string>());
    }

    [Fact]
    public void CreateRequestBody_RejectsInvalidAsin()
    {
        var reference = new AudibleWidevineProviderLicenseReference("CR!reference", 1);

        Assert.Throws<ArgumentException>(() =>
            AudibleWidevineLicenseRefreshProtocol.CreateRequestBody(
                "not-an-asin", reference, [0x01]));
    }

    [Fact]
    public void CreateRequestBody_RejectsInvalidReferenceOrChallenge()
    {
        Assert.Throws<ArgumentException>(() =>
            AudibleWidevineLicenseRefreshProtocol.CreateRequestBody(
                "B012345678",
                new AudibleWidevineProviderLicenseReference(" ", 1),
                [0x01]));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AudibleWidevineLicenseRefreshProtocol.CreateRequestBody(
                "B012345678",
                new AudibleWidevineProviderLicenseReference("CR!reference", -1),
                [0x01]));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AudibleWidevineLicenseRefreshProtocol.CreateRequestBody(
                "B012345678",
                new AudibleWidevineProviderLicenseReference("CR!reference", 1),
                []));
    }

    [Fact]
    public void ParseResponse_ReturnsPerTitleStatusAndLicenseMetadataVerbatim()
    {
        const string response = """
        {
          "request_id": "request-1",
          "license_refresh_responses": [
            {
              "asin": "B012345678",
              "granted_right": "Ownership",
              "access_expiry_date": "2027-03-12T12:00:00Z",
              "license": "opaque-refreshed-widevine-license",
              "license_metadata": {
                "license_id": "license-1",
                "license_refresh_date": "2026-11-12T12:00:00Z",
                "license_removal_date": "2027-01-12T12:00:00Z",
                "license_expiration_date": "2027-02-12T12:00:00Z"
              },
              "status": {
                "status_code": "Granted",
                "error_message": null
              }
            }
          ]
        }
        """;

        var result = AudibleWidevineLicenseRefreshProtocol.ParseResponse(
            response, "B012345678");

        Assert.Equal("B012345678", result.Asin);
        Assert.Equal("Granted", result.StatusCode);
        Assert.Null(result.ErrorMessage);
        Assert.Equal("opaque-refreshed-widevine-license", result.License);
        Assert.Equal("license-1", result.LicenseId);
        Assert.Equal("2027-01-12T12:00:00Z", result.LicenseRemovalDate);
        Assert.Equal("2027-02-12T12:00:00Z", result.LicenseExpirationDate);
        Assert.Equal("Ownership", result.GrantedRight);
        Assert.Equal("2027-03-12T12:00:00Z", result.AccessExpiryDate);
        Assert.Empty(result.LicenseDenialReasons);
    }

    [Fact]
    public void ParseResponse_PreservesDenialWithoutTreatingHttpBatchAsGrant()
    {
        const string response = """
        {
          "license_refresh_responses": [
            {
              "asin": "B012345678",
              "status": {
                "status_code": "Denied",
                "error_message": "entitlement no longer active",
                "license_denial_reasons": [
                  "membership expired",
                  "title not available in this marketplace"
                ]
              }
            }
          ]
        }
        """;

        var result = AudibleWidevineLicenseRefreshProtocol.ParseResponse(
            response, "B012345678");

        Assert.Equal("Denied", result.StatusCode);
        Assert.Equal("entitlement no longer active", result.ErrorMessage);
        Assert.Equal(
            new[] { "membership expired", "title not available in this marketplace" },
            result.LicenseDenialReasons);
        Assert.Null(result.License);
    }

    [Fact]
    public void ParseResponse_RejectsMissingOrDuplicateTitleResults()
    {
        const string missing = """{"license_refresh_responses": []}""";
        const string duplicate = """
        {
          "license_refresh_responses": [
            {"asin": "B012345678"},
            {"asin": "B012345678"}
          ]
        }
        """;

        Assert.Throws<InvalidDataException>(() =>
            AudibleWidevineLicenseRefreshProtocol.ParseResponse(missing, "B012345678"));
        Assert.Throws<InvalidDataException>(() =>
            AudibleWidevineLicenseRefreshProtocol.ParseResponse(duplicate, "B012345678"));
    }

    [Fact]
    public void ParseResponse_RejectsMalformedOrOversizedPayload()
    {
        Assert.Throws<InvalidDataException>(() =>
            AudibleWidevineLicenseRefreshProtocol.ParseResponse("not-json", "B012345678"));
        Assert.Throws<InvalidDataException>(() =>
            AudibleWidevineLicenseRefreshProtocol.ParseResponse(
                new string(' ', 1024 * 1024 + 1), "B012345678"));
    }
}
