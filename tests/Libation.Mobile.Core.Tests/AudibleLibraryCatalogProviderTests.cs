using System.Runtime.CompilerServices;
using AudibleApi;
using AudibleApi.Common;
using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleLibraryCatalogProviderTests
{
    [Fact]
    public async Task GetLibraryAsync_MapsItemsAcrossPagesAndRequestsContributors()
    {
        LibraryOptions? capturedOptions = null;
        var provider = new AudibleLibraryCatalogProvider(options =>
        {
            capturedOptions = options;
            return Pages(
                [new Item { Asin = "A1", Title = "First", Authors = [new Person { Name = "Writer" }] }],
                [new Item { Asin = "A2", Title = "Second", Narrators = [new Person { Name = "Voice" }] }]);
        });

        var result = await provider.GetLibraryAsync();

        Assert.Equal(LibraryOptions.ResponseGroupOptions.Contributors, capturedOptions!.ResponseGroups);
        Assert.Collection(result,
            first =>
            {
                Assert.Equal("A1", first.ProviderId);
                Assert.Equal("Writer", first.Author);
            },
            second =>
            {
                Assert.Equal("A2", second.ProviderId);
                Assert.Equal("Voice", second.Narrator);
            });
    }

    [Fact]
    public async Task GetLibraryAsync_RejectsMoreThanFiveThousandItems()
    {
        var page = Enumerable.Range(0, 5001)
            .Select(index => new Item { Asin = index.ToString(), Title = "Title" })
            .ToArray();
        var provider = new AudibleLibraryCatalogProvider(_ => Pages(page));

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetLibraryAsync());
    }

    [Fact]
    public async Task GetLibraryAsync_ObservesCancellationBeforeFetching()
    {
        var provider = new AudibleLibraryCatalogProvider(_ =>
            throw new InvalidOperationException("Should not fetch after cancellation."));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetLibraryAsync(cancellation.Token));
    }

    [Fact]
    public async Task GetLibraryAsync_StopsBetweenPagesWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new AudibleLibraryCatalogProvider(_ => PagesAndCancel(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetLibraryAsync(cancellation.Token));
    }

    [Fact]
    public async Task GetLibraryAsync_CancellationInterruptsAnInFlightPageRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new AudibleLibraryCatalogProvider(_ => WaitForCancellation(requestStarted, cancellation.Token));

        var sync = provider.GetLibraryAsync(cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync);
    }

    [Fact]
    public async Task GetLibraryAsync_SkipsNullItemsWithinPage()
    {
        var provider = new AudibleLibraryCatalogProvider(_ => Pages(
            [null!, new Item { Asin = "A1", Title = "Valid", Authors = [new Person { Name = "Writer" }] }]));

        var result = await provider.GetLibraryAsync();

        var item = Assert.Single(result);
        Assert.Equal("A1", item.ProviderId);
        Assert.Equal("Writer", item.Author);
    }

    private static async IAsyncEnumerable<Item[]> WaitForCancellation(
        TaskCompletionSource requestStarted,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        requestStarted.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    private static async IAsyncEnumerable<Item[]> PagesAndCancel(CancellationTokenSource cancellation)
    {
        yield return [new Item { Asin = "A1", Title = "First" }];
        cancellation.Cancel();
        await Task.Yield();
        yield return [new Item { Asin = "A2", Title = "Second" }];
    }

    private static async IAsyncEnumerable<Item[]> Pages(params Item[][] pages)
    {
        foreach (var page in pages)
        {
            yield return page;
            await Task.Yield();
        }
    }
}
