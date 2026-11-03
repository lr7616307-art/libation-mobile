using System.ComponentModel;
using System.Runtime.CompilerServices;
using Libation.Mobile.Core;

namespace Libation.Mobile.UI.ViewModels;

/// <summary>
/// Holds only transient browser-handoff state. The response URL is cleared after
/// completion and is never written to settings, navigation state, or logs.
/// </summary>
public sealed class AudibleSignInViewModel : INotifyPropertyChanged
{
    private readonly AccountConnectionService _connection;
    private readonly IExternalBrowserLauncher _browser;
    private string _marketplace = "www.amazon.in";
    private string _responseUrl = string.Empty;
    private string _status = "Checking Audible connection…";
    private string? _challengeId;
    private bool _isBusy;

    public AudibleSignInViewModel(
        AccountConnectionService connection,
        IExternalBrowserLauncher browser)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Marketplace
    {
        get => _marketplace;
        set
        {
            if (_marketplace == value) return;
            _marketplace = value;
            OnPropertyChanged();
        }
    }

    public string ResponseUrl
    {
        get => _responseUrl;
        set
        {
            if (_responseUrl == value) return;
            _responseUrl = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanComplete));
        }
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
        }
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
            OnPropertyChanged(nameof(CanComplete));
        }
    }

    public bool HasChallenge => !string.IsNullOrWhiteSpace(_challengeId);
    public bool CanBegin => !IsBusy;
    public bool CanComplete => !IsBusy && HasChallenge && !string.IsNullOrWhiteSpace(ResponseUrl);

    public async Task LoadStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await _connection.GetStateAsync(cancellationToken);
            Status = state.Status == AccountConnectionStatus.Connected
                ? "Audible account connected. You can start a new sign-in if needed."
                : "Audible is not connected. Sign in to access your library.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Status = "Could not read the saved account status. Your encrypted account data was not changed.";
        }
    }

    public async Task BeginSignInAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            _challengeId = null;
            OnPropertyChanged(nameof(HasChallenge));
            OnPropertyChanged(nameof(CanComplete));
            ResponseUrl = string.Empty;

            var request = new AccountSignInRequest("audible-mobile", Marketplace.Trim());
            var challenge = await _connection.BeginSignInAsync(request, cancellationToken);
            _challengeId = challenge.ChallengeId;
            OnPropertyChanged(nameof(HasChallenge));
            OnPropertyChanged(nameof(CanComplete));

            _browser.Open(challenge.AuthorizationUri);
            Status = "Browser opened. Complete sign-in there, then copy the final address-bar URL and paste it below. Do not share it with anyone.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Sign-in start cancelled.";
            throw;
        }
        catch (Exception)
        {
            _challengeId = null;
            OnPropertyChanged(nameof(HasChallenge));
            OnPropertyChanged(nameof(CanComplete));
            Status = "Could not start Audible sign-in. Check the marketplace and try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task CompleteSignInAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(_challengeId) || string.IsNullOrWhiteSpace(ResponseUrl))
            return;

        var challengeId = _challengeId;
        var responseUrl = ResponseUrl;

        try
        {
            IsBusy = true;
            Status = "Completing secure Audible sign-in…";
            var state = await _connection.CompleteSignInAsync(challengeId, responseUrl, cancellationToken);
            Status = state.Status == AccountConnectionStatus.Connected
                ? "Audible account connected. Your complete identity was saved in encrypted storage."
                : "Sign-in completed, but the account needs attention.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Sign-in cancelled. Start a new attempt to continue.";
            throw;
        }
        catch (Exception)
        {
            Status = "Sign-in could not be completed. The attempt has been discarded; start again. Your response URL was cleared.";
        }
        finally
        {
            _challengeId = null;
            ResponseUrl = string.Empty;
            OnPropertyChanged(nameof(HasChallenge));
            OnPropertyChanged(nameof(CanComplete));
            IsBusy = false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
