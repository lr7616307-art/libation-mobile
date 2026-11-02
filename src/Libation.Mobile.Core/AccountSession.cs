namespace Libation.Mobile.Core;

/// <summary>
/// In-memory representation of an authenticated account session.
/// Secret fields must only be persisted through ISecretStore.
/// </summary>
public sealed record AccountSession(
    string AccountId,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc);
