using System.Text.Json;
using System.Text.Json.Serialization;

namespace Libation.Mobile.Audible;

/// <summary>
/// App-owned, versioned representation of a complete Audible identity.
/// Contains secrets: never log, display, export, or persist this object except
/// as one encrypted ISecretStore value.
/// </summary>
public sealed record AudibleIdentitySnapshot
{
    public const int CurrentSchemaVersion = 2;
    public const string ProviderName = "audible";
    public const string SupportedAudibleApiVersion = "14.1.2";
    public const int MaximumSerializedBytes = 16 * 1024;

    public required int SchemaVersion { get; init; }
    public required string Provider { get; init; }
    public required string AudibleApiVersion { get; init; }
    public required string RegistrationProfileId { get; init; }
    public required string LocaleName { get; init; }
    public required string AccessToken { get; init; }
    public required DateTimeOffset AccessTokenExpiresAtUtc { get; init; }
    public required string RefreshToken { get; init; }
    public required string PrivateKey { get; init; }
    public required string AdpToken { get; init; }
    public required IReadOnlyList<AudibleCookieSnapshot> Cookies { get; init; }
    public string? DeviceSerialNumber { get; init; }
    public string? DeviceType { get; init; }
    public string? AmazonAccountId { get; init; }
    public string? DeviceName { get; init; }
    public string StoreAuthenticationCookie { get; init; } = string.Empty;
}

/// <summary>Cookie pair; duplicates and ordering are significant.</summary>
public sealed record AudibleCookieSnapshot
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

/// <summary>
/// Strict serializer for the encrypted Audible identity payload. This class does
/// not encrypt data; callers must store the returned JSON only via ISecretStore.
/// </summary>
public static class AudibleIdentitySnapshotSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static string Serialize(AudibleIdentitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > AudibleIdentitySnapshot.MaximumSerializedBytes)
            throw new InvalidDataException("Audible identity snapshot exceeds the supported storage limit.");
        return json;
    }

    public static AudibleIdentitySnapshot Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > AudibleIdentitySnapshot.MaximumSerializedBytes)
            throw new InvalidDataException("Audible identity snapshot exceeds the supported storage limit.");

        AudibleIdentitySnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<AudibleIdentitySnapshot>(json, JsonOptions)
                ?? throw new InvalidDataException("Audible identity snapshot is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Audible identity snapshot is malformed or incomplete.", ex);
        }

        Validate(snapshot);
        return snapshot;
    }

    private static void Validate(AudibleIdentitySnapshot snapshot)
    {
        if (snapshot.SchemaVersion != AudibleIdentitySnapshot.CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported Audible identity snapshot version.");
        if (!string.Equals(snapshot.Provider, AudibleIdentitySnapshot.ProviderName, StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported identity provider.");
        if (!string.Equals(snapshot.AudibleApiVersion, AudibleIdentitySnapshot.SupportedAudibleApiVersion, StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported AudibleApi identity format version.");
        if (!string.Equals(snapshot.RegistrationProfileId, AudibleOAuthFlow.RegistrationProfileId, StringComparison.Ordinal))
            throw new InvalidDataException("The saved Audible identity uses an obsolete device-registration profile. Sign in again to register the updated profile.");

        RequireText(snapshot.LocaleName, nameof(snapshot.LocaleName));
        RequirePrefix(snapshot.AccessToken, "Atna|", nameof(snapshot.AccessToken));
        RequirePrefix(snapshot.RefreshToken, "Atnr|", nameof(snapshot.RefreshToken));
        RequireText(snapshot.PrivateKey, nameof(snapshot.PrivateKey));
        RequireText(snapshot.AdpToken, nameof(snapshot.AdpToken));
        if (!snapshot.AdpToken.StartsWith('{') || !snapshot.AdpToken.EndsWith('}'))
            throw new InvalidDataException("ADP token format is invalid.");
        if (snapshot.AccessTokenExpiresAtUtc == default)
            throw new InvalidDataException("Access token expiry is missing.");
        if (snapshot.Cookies is null)
            throw new InvalidDataException("Cookie collection is missing.");

        foreach (var cookie in snapshot.Cookies)
        {
            if (cookie is null)
                throw new InvalidDataException("Cookie entry is null.");
            RequireText(cookie.Name, "Cookie name");
            ArgumentNullException.ThrowIfNull(cookie.Value);
        }

        ArgumentNullException.ThrowIfNull(snapshot.StoreAuthenticationCookie);
    }

    private static void RequirePrefix(string value, string prefix, string field)
    {
        RequireText(value, field);
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException($"{field} has an invalid format.");
    }

    private static void RequireText(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{field} is required.");
    }
}
