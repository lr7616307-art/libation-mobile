using System.ComponentModel;
using System.Runtime.CompilerServices;
using Libation.Mobile.Core;

namespace Libation.Mobile.UI.ViewModels;

/// <summary>
/// Coordinates the in-app Audible sign-in window. Credentials remain on the
/// Amazon page; only the final HTTPS callback URL reaches the auth adapter.
/// </summary>
public sealed class AudibleSignInViewModel : INotifyPropertyChanged
{
    private readonly AccountConnectionService _connection;
    private readonly IExternalBrowserLauncher _browser;
    private string _marketplace = "in";
    private string _status = "Checking Audible connection…";
    private bool _isBusy;
    private AccountConnectionState? _lastState;

    public AudibleSignInViewModel(AccountConnectionService connection, IExternalBrowserLauncher browser)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Marketplace
    {
        get => _marketplace;
        set { if (_marketplace == value) return; _marketplace = value; OnPropertyChanged(); }
    }
    public string Status
    {
        get => _status;
        private set { if (_status == value) return; _status = value; OnPropertyChanged(); }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanBegin));
        }
    }
    public bool CanBegin => !IsBusy;
    public string ConnectionStatusLabel => _lastState?.Status switch
    {
        AccountConnectionStatus.Connected => $"Connected · {_lastState.AccountId}",
        AccountConnectionStatus.Expired => "Session expired",
        AccountConnectionStatus.ReauthenticationRequired => "Sign-in required",
        _ => "Not connected"
    };
    public async Task<string> GetDisplayStateAsync()
    {
        await LoadStatusAsync();
        return Status;
    }

    public async Task LoadStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await _connection.GetStateAsync(cancellationToken);
            _lastState = state;
            OnPropertyChanged(nameof(ConnectionStatusLabel));
            Status = state.Status switch
            {
                AccountConnectionStatus.Connected => "Audible account connected.",
                AccountConnectionStatus.Expired => "Your Audible session has expired. Sign in again to restore access.",
                AccountConnectionStatus.ReauthenticationRequired => "Saved Audible credentials need repair. Sign in again to restore your library.",
                _ => "Sign in to your selected Audible marketplace to access your library."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            Status = "Could not read the saved account status. Your encrypted account data was not changed.";
        }
    }

    public async Task BeginSignInAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        string? challengeId = null;
        try
        {
            IsBusy = true;
            Status = "Preparing secure Audible sign-in…" ;
            var request = new AccountSignInRequest("audible-mobile", Marketplace.Trim());
            var challenge = await _connection.BeginSignInAsync(request, cancellationToken);
            challengeId = challenge.ChallengeId;

            Status = "Sign in on the Amazon page. Your password and verification codes are not read by this app.";
            var responseUrl = await _browser.OpenAsync(challenge.AuthorizationUri, cancellationToken);
            if (string.IsNullOrWhiteSpace(responseUrl))
            {
                await _connection.CancelSignInAsync(challenge.ChallengeId, CancellationToken.None);
                challengeId = null;
                Status = "Sign-in cancelled. Your existing Audible connection, if any, is unchanged.";
                return;
            }

            Status = "Completing secure Audible sign-in…";
            var state = await _connection.CompleteSignInAsync(challenge.ChallengeId, responseUrl, cancellationToken);
            _lastState = state;
            OnPropertyChanged(nameof(ConnectionStatusLabel));
            challengeId = null;
            Status = state.Status == AccountConnectionStatus.Connected
                ? "Audible account connected. Your encrypted account identity is saved."
                : "Sign-in completed, but the account needs attention.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Sign-in cancelled. Start a new attempt to continue.";
            throw;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(challengeId))
            {
                try { await _connection.CancelSignInAsync(challengeId, CancellationToken.None); }
                catch (Exception) { /* The challenge expires automatically if cancellation fails. */ }
            }
            Status = $"Audible sign-in failed ({ex.GetType().Name}). Check your connection and try again.";
        }
        finally { IsBusy = false; }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            await _connection.DisconnectAsync(cancellationToken);
            _lastState = await _connection.GetStateAsync(cancellationToken);
            OnPropertyChanged(nameof(ConnectionStatusLabel));
            Status = "Audible account disconnected. Saved credentials were removed from encrypted storage.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Disconnect cancelled.";
            throw;
        }
        catch (Exception)
        {
            Status = "Could not fully disconnect. Secure account data may still be present; retry disconnect.";
        }
        finally { IsBusy = false; }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
