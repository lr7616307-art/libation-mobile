using AudibleApi.Common;
using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleItemMapperTests
{
    [Fact]
    public void Map_UsesAuthorAndNarratorArraysAndFormatsRuntime()
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
            PublisherName = "Publisher must not become a contributor",
            PublisherSummary = "  Full audiobook description  "
        };

        var result = AudibleItemMapper.Map(item);

        Assert.Equal("B012345678", result.ProviderId);
        Assert.Equal("Example title: Subtitle", result.Title);
        Assert.Equal("Author One, Author Two", result.Author);
        Assert.Equal("Narrator One, Narrator Two", result.Narrator);
        Assert.Equal("5h 21m", result.Length);
        Assert.Equal("Full audiobook description", result.Description);
        Assert.Equal("https://m.media-amazon.com/images/I/cover123._SL500_.jpg", result.CoverImageUrl);
    }

    [Fact]
    public void Map_UsesRawProductTitleAndNeverReturnsTheLibraryApiNullSentinel()
    {
        var item = new Item
        {
            Asin = "B012345678",
            Title = null,
            Subtitle = "A meaningful subtitle",
            Authors = [new Person { Name = "Writer" }]
        };

        var result = AudibleItemMapper.Map(item);

        Assert.Equal("A meaningful subtitle", result.Title);
        Assert.DoesNotContain("[null]", result.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_UsesStableAsinFallbackWhenBothTitleFieldsAreMissing()
    {
        var result = AudibleItemMapper.Map(new Item { Asin = "B012345678" });

        Assert.Equal("Untitled Audible audiobook (B012345678)", result.Title);
    }

    [Fact]
    public void Map_CarriesAudiblePlaybackAndDownloadFlags()
    {
        var result = AudibleItemMapper.Map(new Item
        {
            Asin = "B012345678",
            Title = "Title",
            IsPlayable = true,
            IsDownloaded = false
        });

        Assert.True(result.IsPlayableOnAudible);
        Assert.False(result.IsDownloadedOnAudible);
    }

    [Fact]
    public void Map_FallsBackToAudibleEditorsSummaryAndEditorialReviewForDescription()
    {
        var editorialOnly = AudibleItemMapper.Map(new Item
        {
            Asin = "B012345678",
            Title = "Title",
            AudibleEditorsSummary = "   ",
            EditorialReviews = ["  Editorial description  "]
        });
        Assert.Equal("Editorial description", editorialOnly.Description);

        var editorsSummary = AudibleItemMapper.Map(new Item
        {
            Asin = "B012345678",
            Title = "Title",
            AudibleEditorsSummary = "  Editors summary  ",
            EditorialReviews = ["Editorial review"]
        });
        Assert.Equal("Editors summary", editorsSummary.Description);
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
        Assert.Equal(string.Empty, result.Length);
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

    [Theory]
    [InlineData(45, "45m")]
    [InlineData(60, "1h")]
    [InlineData(121, "2h 1m")]
    [InlineData(0, "")]
    [InlineData(-10, "")]
    public void FormatRuntime_ProducesCompactDuration(double minutes, string expected)
    {
        Assert.Equal(expected, AudibleItemMapper.FormatRuntime(minutes));
    }

    [Fact]
    public void FormatRuntime_RejectsMissingOrNonNumericValues()
    {
        Assert.Equal(string.Empty, AudibleItemMapper.FormatRuntime(null));
        Assert.Equal(string.Empty, AudibleItemMapper.FormatRuntime("unknown"));
        Assert.Equal(string.Empty, AudibleItemMapper.FormatRuntime(double.NaN));
        Assert.Equal(string.Empty, AudibleItemMapper.FormatRuntime(double.PositiveInfinity));
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
