namespace Libation.Mobile.Core;

public enum AccountConnectionStatus
{
    Disconnected,
    Connected,
    Expired,
    ReauthenticationRequired
}

/// <summary>Safe account status for presentation; deliberately contains no credentials.</summary>
public sealed record AccountConnectionState(
    AccountConnectionStatus Status,
    string? AccountId,
    DateTimeOffset? ExpiresAtUtc);
