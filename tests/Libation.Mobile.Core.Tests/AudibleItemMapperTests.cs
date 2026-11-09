using AudibleApi.Common;
using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleItemMapperTests
{
    [Fact]
    public void Map_UsesAuthorAndNarratorArraysAndPreservesTheirOrder()
    {
        var item = new Item
        {
            Asin = " B012345678 ",
            Title = " Example title ",
            Subtitle = " Subtitle ",
            RuntimeLengthMin = 321,
            ProductImages = new ProductImages { The500 = new Uri("https://m.media-amazon.com/images/I/cover123._SL500_.jpg") },
            Authors =
            [
                new Person { Name = " Author One " },
                new Person { Name = "Author Two" }
            ],
            Narrators =
            [
                new Person { Name = "Narrator One" },
                new Person { Name = " Narrator Two " }
            ],
            PublisherName = "Publisher must not become a contributor"
        };

        var result = AudibleItemMapper.Map(item);

        Assert.Equal("B012345678", result.ProviderId);
        Assert.Equal("Example title: Subtitle", result.Title);
        Assert.Equal("Author One, Author Two", result.Author);
        Assert.Equal("Narrator One, Narrator Two", result.Narrator);
        Assert.Equal("321", result.Length);
        Assert.Equal("https://m.media-amazon.com/images/I/cover123._SL500_.jpg", result.CoverImageUrl);
    }

    [Fact]
    public void Map_UsesUnknownAuthorWhenAuthorArrayIsAbsent()
    {
        var item = new Item
        {
            Title = "Title",
            PublisherName = "Publisher name"
        };

        var result = AudibleItemMapper.Map(item);

        Assert.Equal("Unknown author", result.Author);
        Assert.Equal(string.Empty, result.Narrator);
    }

    [Fact]
    public void Map_IgnoresBlankContributorNames()
    {
        var item = new Item
        {
            Authors =
            [
                new Person { Name = "" },
                new Person { Name = "  " },
                new Person { Name = " Author " }
            ],
            Narrators = [new Person { Name = null }]
        };

        var result = AudibleItemMapper.Map(item);

        Assert.Equal("Author", result.Author);
        Assert.Equal(string.Empty, result.Narrator);
    }

    [Fact]
    public void Map_DoesNotInferAuthorOrNarratorFromPublisherOrOtherMetadata()
    {
        var item = new Item
        {
            PublisherName = "Publisher",
            PublisherSummary = "Publisher summary",
            AudibleEditorsSummary = "Editorial summary"
        };

        var result = AudibleItemMapper.Map(item);

        Assert.Equal("Unknown author", result.Author);
        Assert.Equal(string.Empty, result.Narrator);
    }

    [Fact]
    public void JoinNames_PreservesMultipleNamesAndSourceOrder()
    {
        Assert.Equal("First, Second", AudibleItemMapper.JoinNames([" First ", "Second"]));
    }

    [Fact]
    public void JoinNames_EmptyForMissingOrNullCollection()
    {
        Assert.Equal(string.Empty, AudibleItemMapper.JoinNames(null));
        Assert.Equal(string.Empty, AudibleItemMapper.JoinNames([]));
    }
}
