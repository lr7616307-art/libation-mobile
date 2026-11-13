namespace Libation.Mobile.Core;

/// <summary>Non-secret account routing details selected by the user.</summary>
public sealed record AccountSignInRequest(string LoginId, string Marketplace)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(LoginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Marketplace);
    }
}

/// <summary>A provider-created browser cookie required for a short-lived device registration.</summary>
public sealed record ExternalSignInCookie(
    string Name,
    string Value,
    string Domain,
    string Path);

/// <summary>Short-lived browser sign-in handoff. Contains no account password or tokens.</summary>
public sealed record ExternalSignInChallenge(
    string ChallengeId,
    Uri AuthorizationUri,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<ExternalSignInCookie>? SignInCookies = null);
