namespace Libation.Mobile.Core;

public static class ExternalSignInChallengeValidator
{
    public static void Validate(ExternalSignInChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge.ChallengeId);
        if (challenge.AuthorizationUri is null
            || !challenge.AuthorizationUri.IsAbsoluteUri
            || !string.Equals(challenge.AuthorizationUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(challenge.AuthorizationUri.Host))
            throw new ArgumentException("Authorization URI must be an absolute HTTPS URL.", nameof(challenge));
        if (challenge.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new ArgumentException("The sign-in challenge has expired.", nameof(challenge));
    }
}
