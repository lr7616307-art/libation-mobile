using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleItemMapperTests
{
    [Fact]
    public void JoinNames_PreservesMultipleNamesAndSourceOrder()
    {
        var result = AudibleItemMapper.JoinNames([" Author One ", "Author Two"]);

        Assert.Equal("Author One, Author Two", result);
    }

    [Fact]
    public void JoinNames_EmptyForMissingOrNullCollection()
    {
        Assert.Equal(string.Empty, AudibleItemMapper.JoinNames(null));
        Assert.Equal(string.Empty, AudibleItemMapper.JoinNames([]));
    }

    [Fact]
    public void JoinNames_IgnoresBlankNames()
    {
        var result = AudibleItemMapper.JoinNames(["", "  ", null, "Narrator"]);

        Assert.Equal("Narrator", result);
    }

    [Fact]
    public void JoinNames_DoesNotInventContributorsFromUnrelatedFields()
    {
        // Publisher and arbitrary contributor metadata are deliberately not inputs.
        var result = AudibleItemMapper.JoinNames(null);

        Assert.Equal(string.Empty, result);
    }
}
