using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class LocalAudioConversionPolicyTests
{
    [Theory]
    [InlineData("sample.mp3")]
    [InlineData("sample.m4a")]
    [InlineData("sample.m4b")]
    [InlineData("sample.wav")]
    [InlineData("sample.flac")]
    [InlineData("sample.ogg")]
    public void AllowsOrdinaryLocalAudio(string path)
    {
        Assert.True(LocalAudioConversionPolicy.IsSupportedUnprotectedInput(path));
        LocalAudioConversionPolicy.EnsureSupportedUnprotectedInput(path);
    }

    [Theory]
    [InlineData("audible-book.aax")]
    [InlineData("audible-book.aaxc")]
    [InlineData("audible-book.protected")]
    public void ExplicitlyRejectsProtectedAudibleContainers(string path)
    {
        Assert.True(LocalAudioConversionPolicy.IsProtectedContainer(path));
        Assert.False(LocalAudioConversionPolicy.IsSupportedUnprotectedInput(path));
        Assert.Throws<NotSupportedException>(
            () => LocalAudioConversionPolicy.EnsureSupportedUnprotectedInput(path));
    }

    [Fact]
    public void RejectsMissingAndUnknownFormats()
    {
        Assert.False(LocalAudioConversionPolicy.IsSupportedUnprotectedInput(null));
        Assert.False(LocalAudioConversionPolicy.IsSupportedUnprotectedInput("sample.bin"));
        Assert.Throws<NotSupportedException>(
            () => LocalAudioConversionPolicy.EnsureSupportedUnprotectedInput("sample.bin"));
    }
}
