using System.Buffers.Binary;
using System.Net;
using System.Text;
using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleWidevineManifestClientTests
{
    [Fact]
    public async Task FetchAsync_RequestsHttpsAndParsesManifest()
    {
        var xml = CreateManifest();
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https", request.RequestUri?.Scheme);
            Assert.Equal(AudibleApi.Resources.User_Agent,
                request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/dash+xml")
            });
        });
        using var http = new HttpClient(handler);
        var client = new AudibleWidevineManifestClient(http);

        var result = await client.FetchAsync(new Uri("https://media.example.invalid/book/manifest.mpd"));

        Assert.Equal("audio/mp4", result.MimeType);
        Assert.Equal(Convert.FromBase64String(GetPsshBase64()), result.InitializationData);
    }

    [Fact]
    public async Task FetchAsync_RejectsHttpManifestUriBeforeRequest()
    {
        var called = false;
        using var http = new HttpClient(new StubHandler((_, _) =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var client = new AudibleWidevineManifestClient(http);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.FetchAsync(new Uri("http://media.example.invalid/manifest.mpd")));

        Assert.False(called);
    }

    [Fact]
    public async Task FetchAsync_ReportsOnlyStatusCodeForHttpFailure()
    {
        using var http = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var client = new AudibleWidevineManifestClient(http);
        var uri = new Uri("https://media.example.invalid/manifest.mpd?token=do-not-log");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.FetchAsync(uri));

        Assert.Contains("503", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_RejectsOversizedManifest()
    {
        var body = new StringContent(new string('x', AudibleWidevineManifestClient.MaximumManifestBytes + 1));
        using var http = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body })));
        var client = new AudibleWidevineManifestClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.FetchAsync(new Uri("https://media.example.invalid/manifest.mpd")));
    }

    [Fact]
    public async Task FetchAsync_ObservesCancellationBeforeNetworkRequest()
    {
        var called = false;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = new HttpClient(new StubHandler((_, _) =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var client = new AudibleWidevineManifestClient(http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.FetchAsync(new Uri("https://media.example.invalid/manifest.mpd"), cancellation.Token));

        Assert.False(called);
    }

    private static string CreateManifest()
        => $"""
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" xmlns:cenc="urn:mpeg:cenc:2013">
              <Period>
                <AdaptationSet mimeType="audio/mp4">
                  <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed">
                    <cenc:pssh>{GetPsshBase64()}</cenc:pssh>
                  </ContentProtection>
                  <Representation mimeType="audio/mp4" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

    private static string GetPsshBase64()
    {
        byte[] data = [0x01, 0x02];
        var pssh = new byte[32 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(pssh.AsSpan(0, 4), (uint)pssh.Length);
        "pssh"u8.CopyTo(pssh.AsSpan(4, 4));
        Convert.FromHexString("edef8ba979d64acea3c827dcd51d21ed").CopyTo(pssh.AsSpan(12, 16));
        BinaryPrimitives.WriteUInt32BigEndian(pssh.AsSpan(28, 4), (uint)data.Length);
        data.CopyTo(pssh.AsSpan(32));
        return Convert.ToBase64String(pssh);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
