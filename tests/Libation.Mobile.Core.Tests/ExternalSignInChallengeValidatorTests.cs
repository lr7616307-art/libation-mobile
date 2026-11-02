using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class ExternalSignInChallengeValidatorTests
{
    [Fact]
    public void Validate_AcceptsUnexpiredHttpsChallenge()
    {
        var challenge = new ExternalSignInChallenge(
            "challenge", new Uri("https://example.invalid/authorize?state=opaque"),
            DateTimeOffset.UtcNow.AddMinutes(3));

        ExternalSignInChallengeValidator.Validate(challenge);
    }

    [Theory]
    [InlineData("http://example.invalid/authorize")]
    [InlineData("file:///tmp/login")]
    [InlineData("javascript:alert(1)")]
    public void Validate_RejectsNonHttpsAuthorizationUris(string value)
    {
        var challenge = new ExternalSignInChallenge(
            "challenge", new Uri(value), DateTimeOffset.UtcNow.AddMinutes(3));

        Assert.Throws<ArgumentException>(() => ExternalSignInChallengeValidator.Validate(challenge));
    }

    [Fact]
    public void Validate_RejectsExpiredChallenge()
    {
        var challenge = new ExternalSignInChallenge(
            "challenge", new Uri("https://example.invalid/authorize"),
            DateTimeOffset.UtcNow.AddSeconds(-1));

        Assert.Throws<ArgumentException>(() => ExternalSignInChallengeValidator.Validate(challenge));
    }

    [Fact]
    public void Validate_RejectsEmptyChallengeId()
    {
        var challenge = new ExternalSignInChallenge(
            " ", new Uri("https://example.invalid/authorize"),
            DateTimeOffset.UtcNow.AddMinutes(3));

        Assert.Throws<ArgumentException>(() => ExternalSignInChallengeValidator.Validate(challenge));
    }
}
