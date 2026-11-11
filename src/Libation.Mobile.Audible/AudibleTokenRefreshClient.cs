using AudibleApi;
using AudibleApi.Authorization;
using Dinah.Core;
using Dinah.Core.Security;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Libation.Mobile.Audible;

public sealed record AudibleTokenRefreshResult(AccessToken AccessToken, RefreshToken? RefreshToken);

public interface IAudibleTokenRefresher
{
    Task<AudibleTokenRefreshResult> RefreshAsync(
        Locale locale,
        RefreshToken refreshToken,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Refreshes Audible access tokens using the current LibriSync iPhone-profile
/// request version. This avoids inheriting stale app-version constants from the
/// pinned AudibleApi package.
/// </summary>
public sealed class AudibleTokenRefreshClient(
    HttpClient httpClient,
    ISystemDateTime systemDateTime) : IAudibleTokenRefresher
{
    private const string AppName = "Audible";
    private const string AppVersion = "4.60";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<AudibleTokenRefreshResult> RefreshAsync(
        Locale locale,
        RefreshToken refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locale);
        ArgumentNullException.ThrowIfNull(refreshToken);
        cancellationToken.ThrowIfCancellationRequested();

        var registrationUri = locale.RegistrationUri();
        var tokenUri = new Uri(registrationUri, "/auth/token");
        var form = new Dictionary<string, string>
        {
            ["app_name"] = AppName,
            ["app_version"] = AppVersion,
            ["source_token"] = refreshToken.Reveal(),
            ["source_token_type"] = "refresh_token",
            ["requested_token_type"] = "access_token"
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, tokenUri)
        {
            Content = new FormUrlEncodedContent(form)
        };
        message.Headers.TryAddWithoutValidation(
            "x-amzn-identity-auth-domain", registrationUri.Host);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Audible token refresh failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<RefreshResponse>(
            JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Audible returned an empty token-refresh response.");

        if (string.IsNullOrWhiteSpace(payload.AccessToken)
            || !payload.AccessToken.StartsWith("Atna|", StringComparison.Ordinal))
            throw new InvalidDataException("Audible token-refresh response did not contain a valid access token.");
        if (!TryReadExpiresIn(payload.ExpiresIn, out var expiresIn))
            throw new InvalidDataException("Audible returned an invalid access-token lifetime.");

        RefreshToken? refreshedRefreshToken = null;
        if (!string.IsNullOrWhiteSpace(payload.RefreshToken))
        {
            try
            {
                refreshedRefreshToken = new RefreshToken(new SecretString(payload.RefreshToken));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("Audible returned an invalid refresh token.", ex);
            }
        }

        var expiry = systemDateTime.UtcNow.AddSeconds(expiresIn);
        var accessToken = new AccessToken(new SecretString(payload.AccessToken), expiry);
        return new AudibleTokenRefreshResult(accessToken, refreshedRefreshToken);
    }

    private static bool TryReadExpiresIn(JsonElement value, out long seconds)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt64(out seconds) && seconds > 0;
        if (value.ValueKind == JsonValueKind.String)
            return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)
                && seconds > 0;
        seconds = 0;
        return false;
    }

    private sealed class RefreshResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [JsonPropertyName("expires_in")] public JsonElement ExpiresIn { get; init; }
    }
}
