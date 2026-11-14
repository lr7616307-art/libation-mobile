using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class WidevineProvisioningRequestUriTests
{
    [Fact]
    public void Create_AppendsEncodedSignedRequestToProviderHttpsUrl()
    {
        var requestData = Encoding.UTF8.GetBytes("abc+/=signed-request");

        var uri = WidevineProvisioningRequestUri.Create(
            "https://provision.example.invalid/getcertificate?client=android",
            requestData);

        Assert.Equal("https", uri.Scheme);
        Assert.Contains("client=android", uri.Query);
        Assert.Contains("signedRequest=abc%2B%2F%3Dsigned-request", uri.Query);
    }

    [Fact]
    public void Create_UsesQuestionMarkWhenDefaultUrlHasNoQuery()
    {
        var uri = WidevineProvisioningRequestUri.Create(
            "https://provision.example.invalid/getcertificate",
            Encoding.UTF8.GetBytes("signed"));

        Assert.Equal("?signedRequest=signed", uri.Query);
    }

    [Theory]
    [InlineData("http://provision.example.invalid/getcertificate")]
    [InlineData("file:///tmp/provision")]
    [InlineData("not-a-url")]
    public void Create_RejectsNonHttpsOrInvalidProviderEndpoints(string url)
    {
        Assert.Throws<InvalidDataException>(() =>
            WidevineProvisioningRequestUri.Create(url, Encoding.UTF8.GetBytes("signed")));
    }

    [Fact]
    public void Create_RejectsEmptyOrOversizedProvisioningRequest()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WidevineProvisioningRequestUri.Create(
                "https://provision.example.invalid/getcertificate", []));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WidevineProvisioningRequestUri.Create(
                "https://provision.example.invalid/getcertificate",
                new byte[WidevineProvisioningRequestUri.MaximumRequestBytes + 1]));
    }
}
