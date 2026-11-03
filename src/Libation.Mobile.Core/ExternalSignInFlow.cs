namespace Libation.Mobile.Core;

/// <summary>
/// In-memory state machine for validating a single external-browser callback.
/// It deliberately does not retain the callback URL, cookies, or response parameters.
/// </summary>
public sealed class ExternalSignInFlow
{
    private readonly ExternalSignInChallenge _challenge;
    private readonly Uri _callbackUri;
    private readonly string _expectedState;
    private readonly Func<DateTimeOffset> _utcNow;
    private ExternalSignInFlowStatus _status = ExternalSignInFlowStatus.Pending;

    public ExternalSignInFlow(
        ExternalSignInChallenge challenge,
        Uri callbackUri,
        Func<DateTimeOffset>? utcNow = null)
    {
        ExternalSignInChallengeValidator.Validate(challenge);
        ArgumentNullException.ThrowIfNull(callbackUri);
        if (!callbackUri.IsAbsoluteUri || string.IsNullOrWhiteSpace(callbackUri.Scheme)
            || string.IsNullOrWhiteSpace(callbackUri.Host)
            || !string.IsNullOrEmpty(callbackUri.UserInfo)
            || !string.IsNullOrEmpty(callbackUri.Query)
            || !string.IsNullOrEmpty(callbackUri.Fragment))
            throw new ArgumentException("Callback URI must be an absolute origin/path without user info, query, or fragment.", nameof(callbackUri));

        _expectedState = ReadSingleState(challenge.AuthorizationUri)
            ?? throw new ArgumentException("Authorization URI must contain exactly one state value.", nameof(challenge));
        _challenge = challenge;
        _callbackUri = callbackUri;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public ExternalSignInFlowStatus Status
    {
        get
        {
            ExpireIfNeeded();
            return _status;
        }
    }

    /// <summary>Validates a callback without retaining its URL or query values.</summary>
    public ExternalSignInFlowStatus AcceptCallback(string? callbackUrl)
    {
        ExpireIfNeeded();
        if (_status != ExternalSignInFlowStatus.Pending)
            return _status == ExternalSignInFlowStatus.Completed
                ? ExternalSignInFlowStatus.Invalid
                : _status;

        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var callback)
            || !string.Equals(callback.Scheme, _callbackUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(callback.Host, _callbackUri.Host, StringComparison.OrdinalIgnoreCase)
            || callback.Port != _callbackUri.Port
            || !string.Equals(callback.AbsolutePath, _callbackUri.AbsolutePath, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(callback.UserInfo)
            || !string.IsNullOrEmpty(callback.Fragment)
            || !string.Equals(ReadSingleState(callback), _expectedState, StringComparison.Ordinal))
        {
            _status = ExternalSignInFlowStatus.Invalid;
            return _status;
        }

        _status = ExternalSignInFlowStatus.Completed;
        return _status;
    }

    public ExternalSignInFlowStatus Cancel()
    {
        ExpireIfNeeded();
        if (_status == ExternalSignInFlowStatus.Pending)
            _status = ExternalSignInFlowStatus.Cancelled;
        return _status;
    }

    private void ExpireIfNeeded()
    {
        if (_status == ExternalSignInFlowStatus.Pending
            && _utcNow().ToUniversalTime() >= _challenge.ExpiresAtUtc.ToUniversalTime())
            _status = ExternalSignInFlowStatus.Expired;
    }

    private static string? ReadSingleState(Uri uri)
    {
        string? state = null;
        try
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (!string.Equals(Uri.UnescapeDataString(pair[0].Replace("+", " ")), "state", StringComparison.Ordinal))
                    continue;
                if (state is not null || pair.Length != 2)
                    return null;
                state = Uri.UnescapeDataString(pair[1].Replace("+", " "));
                if (string.IsNullOrWhiteSpace(state))
                    return null;
            }
        }
        catch (UriFormatException)
        {
            return null;
        }
        return state;
    }
}

public enum ExternalSignInFlowStatus
{
    Pending,
    Completed,
    Cancelled,
    Expired,
    Invalid
}
