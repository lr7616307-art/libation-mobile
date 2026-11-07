using System.Net;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudioFileDownloaderTests
{
    [Fact]
    public async Task DownloadAsync_WritesResponseAndReportsProgress()
    {
        using var directory = new TemporaryDirectory();
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        using var client = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            }));
        var downloader = new AudioFileDownloader(client);
        var progress = new RecordingProgress<AudioDownloadProgress>();
        var destination = Path.Combine(directory.Path, "book.m4b");

        var result = await downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination, progress);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(payload.Length, result.BytesReceived);
        Assert.Equal(destination, result.FilePath);
        Assert.NotEmpty(progress.Values);
        Assert.Equal(100d, progress.Values[^1].Percentage);
    }


    [Fact]
    public async Task DownloadAsync_ResumesExistingPartialFileWithRangeRequest()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(partial, new byte[] { 1, 2 });
        await File.WriteAllTextAsync(partial + ".validator", "etag:\"v1\"");
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal(2, request.Headers.Range?.Ranges.Single().From);
            Assert.Equal("\"v1\"", request.Headers.IfRange?.EntityTag?.Tag);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[] { 3, 4, 5 })
            };
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(2, 4, 5);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        var result = await downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(destination));
        Assert.Equal(5, result.BytesReceived);
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task DownloadAsync_ResumesPartialFileWithMatchingLastModifiedValidator()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        var modified = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(partial, new byte[] { 1, 2 });
        await File.WriteAllTextAsync(
            partial + ".validator",
            "modified:" + modified.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal(2, request.Headers.Range?.Ranges.Single().From);
            Assert.Equal(modified, request.Headers.IfRange?.Date);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[] { 3, 4, 5 })
            };
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(2, 4, 5);
            response.Content.Headers.LastModified = modified;
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        var result = await downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(destination));
        Assert.Equal(5, result.BytesReceived);
        Assert.False(File.Exists(partial));
        Assert.False(File.Exists(partial + ".validator"));
    }

    [Fact]
    public async Task DownloadAsync_ResumesWithMatchingLastModifiedValidator()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(destination + ".part", new byte[] { 1, 2 });
        await File.WriteAllTextAsync(destination + ".part.validator", "modified:Tue, 03 Nov 2026 10:00:00 GMT");
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal(2, request.Headers.Range?.Ranges.Single().From);
            Assert.Equal(
                new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero),
                request.Headers.IfRange?.Date);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[] { 3, 4, 5 })
            };
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(2, 4, 5);
            response.Content.Headers.LastModified =
                new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        var result = await downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(destination));
        Assert.Equal(5, result.BytesReceived);
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_RejectsPartialResponseForChangedEntityTag()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(partial, new byte[] { 1, 2 });
        await File.WriteAllTextAsync(partial + ".validator", "etag:\"old-version\"");
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal("\"old-version\"", request.Headers.IfRange?.EntityTag?.Tag);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[] { 3, 4, 5 })
            };
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(2, 4, 5);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"new-version\"");
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(partial));
        Assert.Equal("etag:\"old-version\"", await File.ReadAllTextAsync(partial + ".validator"));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_PromotesCompletePartialAfterRangeNotSatisfiableWhenValidatorMatches()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(destination + ".part", payload);
        await File.WriteAllTextAsync(destination + ".part.validator", "etag:\"complete-v1\"");
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal(payload.Length, request.Headers.Range?.Ranges.Single().From);
            Assert.Equal("\"complete-v1\"", request.Headers.IfRange?.EntityTag?.Tag);
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(payload.Length);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"complete-v1\"");
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        var result = await downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(payload.Length, result.BytesReceived);
        Assert.False(File.Exists(destination + ".part"));
        Assert.False(File.Exists(destination + ".part.validator"));
    }

    [Fact]
    public async Task DownloadAsync_RejectsInsecureRedirectBeforePromotingCompletePartial()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(destination + ".part", payload);
        await File.WriteAllTextAsync(destination + ".part.validator", "etag:\"complete-v1\"");
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get, new Uri("http://provider.example/insecure-audio"))
            };
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(payload.Length);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"complete-v1\"");
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination + ".part"));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_PromotesCompletePartialAfterRangeNotSatisfiableWhenLastModifiedMatches()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var modified = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(destination + ".part", payload);
        await File.WriteAllTextAsync(
            destination + ".part.validator",
            "modified:" + modified.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal(payload.Length, request.Headers.Range?.Ranges.Single().From);
            Assert.Equal(modified, request.Headers.IfRange?.Date);
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(payload.Length);
            response.Content.Headers.LastModified = modified;
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        var result = await downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(payload.Length, result.BytesReceived);
        Assert.False(File.Exists(destination + ".part"));
        Assert.False(File.Exists(destination + ".part.validator"));
    }

    [Fact]
    public async Task DownloadAsync_DoesNotPromotePartialWhenRangeNotSatisfiableOmitsTotalLength()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(partial, payload);
        await File.WriteAllTextAsync(partial + ".validator", "etag:\"stable-v1\"");
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal(payload.Length, request.Headers.Range?.Ranges.Single().From);
            Assert.Equal("\"stable-v1\"", request.Headers.IfRange?.EntityTag?.Tag);
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"stable-v1\"");
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal(payload, await File.ReadAllBytesAsync(partial));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_DoesNotPromotePartialWhenRangeTotalExceedsPartialLength()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(partial, payload);
        await File.WriteAllTextAsync(partial + ".validator", "etag:\"stable-v1\"");
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal(payload.Length, request.Headers.Range?.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(payload.Length + 1);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"stable-v1\"");
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal(payload, await File.ReadAllBytesAsync(partial));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_DoesNotPromoteCompletePartialWhenLastModifiedDiffers()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var savedModified = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        var responseModified = savedModified.AddSeconds(1);
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(destination + ".part", payload);
        await File.WriteAllTextAsync(
            destination + ".part.validator",
            "modified:" + savedModified.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(payload.Length);
            response.Content.Headers.LastModified = responseModified;
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination + ".part"));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_DoesNotPromoteCompletePartialWhenRangeValidatorDiffers()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(destination + ".part", payload);
        await File.WriteAllTextAsync(destination + ".part.validator", "etag:\"old-version\"");
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(payload.Length);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"new-version\"");
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination + ".part"));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_RejectsPartialResponseWhenEntityTagIsMissing()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(destination + ".part", new byte[] { 1, 2 });
        await File.WriteAllTextAsync(destination + ".part.validator", "etag:\"v1\"");
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[] { 3, 4 })
            };
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(2, 3, 4);
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(destination + ".part"));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_RejectsPartialResponseWhenLastModifiedIsMissing()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(destination + ".part", new byte[] { 1, 2 });
        await File.WriteAllTextAsync(destination + ".part.validator", "modified:Tue, 03 Nov 2026 10:00:00 GMT");
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[] { 3, 4 })
            };
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(2, 3, 4);
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(destination + ".part"));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_DiscardsPartialWithMalformedValidatorAndStartsFresh()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        var validator = partial + ".validator";
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(partial, new byte[] { 9, 9, 9 });
        await File.WriteAllTextAsync(validator, "etag:not-a-valid-entity-tag");
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Null(request.Headers.Range);
            Assert.Null(request.Headers.IfRange);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
            };
        }));
        var downloader = new AudioFileDownloader(client);

        var result = await downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(destination));
        Assert.Equal(4, result.BytesReceived);
        Assert.False(File.Exists(partial));
        Assert.False(File.Exists(validator));
    }

    [Fact]
    public async Task DownloadAsync_DiscardsPartialWithoutValidatorInsteadOfRiskingMixedContent()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(destination + ".part", new byte[] { 9, 9 });
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Null(request.Headers.Range);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            };
        }));
        var downloader = new AudioFileDownloader(client);

        await downloader.DownloadAsync(new Uri("https://provider.example/audio"), destination);

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task DownloadAsync_RestartsWhenServerIgnoresRangeRequest()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(destination + ".part", new byte[] { 9, 9 });
        await File.WriteAllTextAsync(destination + ".part.validator", "etag:\"v1\"");
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.NotNull(request.Headers.Range);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            };
        }));
        var downloader = new AudioFileDownloader(client);

        await downloader.DownloadAsync(new Uri("https://provider.example/audio"), destination);

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task DownloadAsync_RejectsEmptySuccessfulResponseAndPreservesExistingDestination()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        await File.WriteAllTextAsync(destination, "existing");
        using var client = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.True(File.Exists(partial));
        Assert.Empty(await File.ReadAllBytesAsync(partial));
    }

    [Fact]
    public async Task DownloadAsync_CancellationDurablyCheckpointsAcceptedBytes()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        var payload = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            }));
        var downloader = new AudioFileDownloader(client);
        var progress = new CancelAfterBytesProgress(cancellation, 1024 * 1024);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"),
            destination,
            progress,
            cancellation.Token));

        Assert.True(File.Exists(partial));
        var savedPartial = await File.ReadAllBytesAsync(partial);
        Assert.True(savedPartial.Length >= 1024 * 1024);
        Assert.Equal(payload[..savedPartial.Length], savedPartial);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_CancellationKeepsPartialAndPreservesExistingDestination()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        await File.WriteAllTextAsync(destination, "existing");
        var payload = Enumerable.Range(0, 128 * 1024).Select(i => (byte)(i % 251)).ToArray();
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            }));
        var downloader = new AudioFileDownloader(client);
        var progress = new CancelAfterFirstProgress(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"),
            destination,
            progress,
            cancellation.Token));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.True(File.Exists(partial));
        var savedPartial = await File.ReadAllBytesAsync(partial);
        Assert.NotEmpty(savedPartial);
        Assert.True(savedPartial.Length < payload.Length);
        Assert.Equal(payload[..savedPartial.Length], savedPartial);
    }

    [Fact]
    public async Task DownloadAsync_RejectsTruncatedResponseAndKeepsPartialForRetry()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        var payload = new byte[] { 1, 2, 3 };
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentLength = 5;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.False(File.Exists(destination));
        Assert.Equal(payload, await File.ReadAllBytesAsync(partial));
    }

    [Fact]
    public async Task DownloadAsync_RejectsResumeResponseStartingAtWrongOffset()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(partial, new byte[] { 1, 2 });
        await File.WriteAllTextAsync(partial + ".validator", "etag:\"v1\"");
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[] { 3, 4, 5 })
            };
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(1, 3, 5);
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(partial));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_RejectsNonHttpsUrl()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)));
        var downloader = new AudioFileDownloader(client);
        using var directory = new TemporaryDirectory();

        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync(
            new Uri("http://provider.example/audio"),
            Path.Combine(directory.Path, "book.m4b")));
    }

    [Fact]
    public async Task DownloadAsync_FollowsRelativeHttpsRedirect()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var calls = 0;
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal("https://provider.example/audio", request.RequestUri!.AbsoluteUri);
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("/cdn/book", UriKind.Relative);
                return redirect;
            }

            Assert.Equal("https://provider.example/cdn/book", request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            };
        }));
        var downloader = new AudioFileDownloader(client);

        await downloader.DownloadAsync(new Uri("https://provider.example/audio"), destination);

        Assert.Equal(2, calls);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task DownloadAsync_PreservesRangeValidatorAcrossHttpsRedirect()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(destination + ".part", new byte[] { 1, 2 });
        await File.WriteAllTextAsync(destination + ".part.validator", "etag:\"v1\"");
        var calls = 0;
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            calls++;
            Assert.Equal(2, request.Headers.Range?.Ranges.Single().From);
            Assert.Equal("\\"v1\\"", request.Headers.IfRange?.EntityTag?.Tag);
            if (calls == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new Uri("https://cdn.example/audio");
                return redirect;
            }

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[] { 3, 4, 5 })
            };
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(2, 4, 5);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\\"v1\\"");
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        await downloader.DownloadAsync(new Uri("https://provider.example/audio"), destination);

        Assert.Equal(2, calls);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task DownloadAsync_EnforcesMaximumRedirectCount()
    {
        using var directory = new TemporaryDirectory();
        var calls = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            calls++;
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri($"https://cdn.example/audio/{calls}");
            return redirect;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"),
            Path.Combine(directory.Path, "book.m4b")));

        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task DownloadAsync_RejectsHttpsToHttpRedirectBeforeFollowingIt()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var calls = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            calls++;
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("http://insecure.example/audio");
            return redirect;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal(1, calls);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_RejectsRedirectWithEmbeddedCredentials()
    {
        using var directory = new TemporaryDirectory();
        var calls = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            calls++;
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("https://user:secret@cdn.example/audio");
            return redirect;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"),
            Path.Combine(directory.Path, "book.m4b")));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DownloadAsync_RejectsRedirectWithoutLocation()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.Redirect)));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"),
            Path.Combine(directory.Path, "book.m4b")));
    }

    [Fact]
    public async Task DownloadAsync_StopsRedirectLoop()
    {
        using var directory = new TemporaryDirectory();
        var calls = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            calls++;
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("https://provider.example/audio");
            return redirect;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"),
            Path.Combine(directory.Path, "book.m4b")));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DownloadAsync_RejectsRedirectToNonHttpsAndPreservesDestination()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var destination = Path.Combine(directory.Path, "book.m4b");
        await File.WriteAllTextAsync(destination, "existing");
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get, new Uri("http://provider.example/insecure-audio"))
            };
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.part"));
    }

    [Fact]
    public async Task DownloadAsync_StopsWhenStreamExceedsConfiguredSizeLimit()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(destination, "existing");
        using var client = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 })
            }));
        var downloader = new AudioFileDownloader(client, maximumBytes: 4);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.part"));
    }

    [Fact]
    public async Task DownloadAsync_DoesNotReplaceDestinationWhenRequestFails()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var destination = Path.Combine(directory.Path, "book.m4b");
        await File.WriteAllTextAsync(destination, "existing");
        using var client = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.part"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request, cancellationToken));
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class CancelAfterBytesProgress(
        CancellationTokenSource cancellation, long threshold)
        : IProgress<AudioDownloadProgress>
    {
        public void Report(AudioDownloadProgress value)
        {
            if (value.BytesReceived >= threshold)
                cancellation.Cancel();
        }
    }

    private sealed class CancelAfterFirstProgress(CancellationTokenSource cancellation)
        : IProgress<AudioDownloadProgress>
    {
        private bool _cancelled;

        public void Report(AudioDownloadProgress value)
        {
            if (_cancelled) return;
            _cancelled = true;
            cancellation.Cancel();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "LibationMobileDownloadTests", Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
