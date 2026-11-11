using System.ComponentModel;
using System.Runtime.CompilerServices;
using Libation.Mobile.Core;

namespace Libation.Mobile.UI.ViewModels;

/// <summary>
/// Coordinates Audible sign-in inside VØRATH. The in-app Audible page handles
/// credentials; only its validated final HTTPS callback reaches the auth adapter.
/// </summary>
public sealed class AudibleSignInViewModel : INotifyPropertyChanged
{
    private readonly AccountConnectionService _connection;
    private readonly IExternalBrowserLauncher _browser;
    private string _marketplace = "in";
    private string _status = "Checking Audible connection…";
    private bool _isBusy;
    private AccountConnectionState? _lastState;
    private int _syncedAudiobookCount;
    private DateTimeOffset? _lastLibrarySync;
    private string _librarySyncStatus = "No successful Audible library sync yet.";

    public AudibleSignInViewModel(AccountConnectionService connection, IExternalBrowserLauncher browser)
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
            OnPropertyChanged(nameof(MarketplaceLabel));
        }
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
    public bool HasConnectedAccount => _lastState?.Status == AccountConnectionStatus.Connected;
    public bool CanAddAccount => !HasConnectedAccount && !IsBusy;
    public bool CanSyncLibrary => HasConnectedAccount && !IsBusy;
    public string ConnectionStatusLabel => _lastState?.Status switch
    {
        AccountConnectionStatus.Connected => "Connected",
        AccountConnectionStatus.Expired => "Session expired",
        AccountConnectionStatus.ReauthenticationRequired => "Sign-in required",
        _ => "Not connected"
    };
    public string AccountDisplayName => HasConnectedAccount ? "Audible account" : "No Audible account connected";
    public string AccountIdLabel
    {
        get
        {
            var accountId = _lastState?.AccountId;
            if (!HasConnectedAccount || string.IsNullOrWhiteSpace(accountId) || accountId == "audible-account")
                return "Amazon account ID not supplied";
            var suffix = accountId.Length <= 4 ? accountId : accountId[^4..];
            return $"Amazon account ID ····{suffix}";
        }
    }
    public string MarketplaceLabel
    {
        get
        {
            var (name, domain) = Marketplace.ToLowerInvariant() switch
            {
                "in" => ("India", "audible.in"),
                "us" => ("United States", "audible.com"),
                "uk" => ("United Kingdom", "audible.co.uk"),
                "de" => ("Germany", "audible.de"),
                "fr" => ("France", "audible.fr"),
                "ca" => ("Canada", "audible.ca"),
                "au" => ("Australia", "audible.com.au"),
                "it" => ("Italy", "audible.it"),
                "es" => ("Spain", "audible.es"),
                "jp" => ("Japan", "audible.co.jp"),
                "br" => ("Brazil", "audible.com.br"),
                _ => ("Selected marketplace", "Audible")
            };
            return $"Marketplace · {name} ({domain})";
        }
    }
    public string SyncedAudiobookCountLabel => _lastLibrarySync is null
        ? "Library not synced yet"
        : $"{_syncedAudiobookCount} audiobooks synced";
    public string LibrarySyncStatusLabel => _librarySyncStatus;
    public string LastLibrarySyncLabel => _lastLibrarySync is null
        ? "No successful sync yet"
        : $"Last synced · {_lastLibrarySync.Value.ToLocalTime():g}";

    public void UpdateLibrarySummary(int count, DateTimeOffset? lastSync, string status)
    {
        _syncedAudiobookCount = Math.Max(0, count);
        _lastLibrarySync = lastSync?.ToUniversalTime();
        _librarySyncStatus = string.IsNullOrWhiteSpace(status) ? "Library sync status unavailable" : status;
        OnPropertyChanged(nameof(SyncedAudiobookCountLabel));
        OnPropertyChanged(nameof(LibrarySyncStatusLabel));
        OnPropertyChanged(nameof(LastLibrarySyncLabel));
    }

    private void NotifyAccountSummaryChanged()
    {
        OnPropertyChanged(nameof(HasConnectedAccount));
        OnPropertyChanged(nameof(CanAddAccount));
        OnPropertyChanged(nameof(CanSyncLibrary));
        OnPropertyChanged(nameof(ConnectionStatusLabel));
        OnPropertyChanged(nameof(AccountDisplayName));
        OnPropertyChanged(nameof(AccountIdLabel));
        OnPropertyChanged(nameof(MarketplaceLabel));
    }

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
            NotifyAccountSummaryChanged();
            Status = state.Status switch
            {
                AccountConnectionStatus.Connected => "Audible account connected.",
                AccountConnectionStatus.Expired => "Your Audible session has expired. Sign in again to restore access.",
                AccountConnectionStatus.ReauthenticationRequired => "Saved Audible credentials need repair. Sign in again to restore your library.",
                _ => "Sign in to Audible inside VØRATH to access your library."
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

            // India is the default. Android presents India, US and UK choices
            // before generating the marketplace-specific Audible challenge.
            if (_browser is IAudibleMarketplaceSelector selector)
            {
                var selectedMarketplace = await selector.SelectMarketplaceAsync(Marketplace, cancellationToken);
                if (string.IsNullOrWhiteSpace(selectedMarketplace))
                {
                    Status = "Audible sign-in cancelled before opening the sign-in page.";
                    return;
                }
                Marketplace = selectedMarketplace;
            }

            Status = $"Preparing secure Audible {Marketplace.ToUpperInvariant()} sign-in…";
            var request = new AccountSignInRequest("audible-mobile", Marketplace.Trim());
            var challenge = await _connection.BeginSignInAsync(request, cancellationToken);
            challengeId = challenge.ChallengeId;

            Status = "Complete sign-in on the Audible page inside VØRATH. Your password and verification codes stay on that sign-in page.";
            var responseUrl = await _browser.OpenAsync(challenge.AuthorizationUri, cancellationToken);
            if (string.IsNullOrWhiteSpace(responseUrl))
            {
                await _connection.CancelSignInAsync(challenge.ChallengeId, CancellationToken.None);
                challengeId = null;
                Status = "Sign-in cancelled. Your existing Audible connection, if any, is unchanged.";
                return;
            }

            Status = "Completing secure Audible device registration…";
            var state = await _connection.CompleteSignInAsync(challenge.ChallengeId, responseUrl, cancellationToken);
            _lastState = state;
            NotifyAccountSummaryChanged();
            challengeId = null;
            if (state.Status == AccountConnectionStatus.Connected)
            {
                UpdateLibrarySummary(0, null, "Account connected. Sync your Audible library to load its titles.");
                Status = "Audible account connected. Your encrypted device identity is saved.";
            }
            else
            {
                Status = "Sign-in completed, but the account needs attention.";
            }
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
            var detail = ex.Message
                .Replace("Atna|", "[token-redacted]", StringComparison.Ordinal)
                .Replace("Atnr|", "[token-redacted]", StringComparison.Ordinal);
            if (detail.Length > 180) detail = detail[..177] + "…";
            Status = string.IsNullOrWhiteSpace(detail)
                ? $"Audible sign-in failed ({ex.GetType().Name})."
                : $"Audible sign-in failed: {detail}";
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
            NotifyAccountSummaryChanged();
            UpdateLibrarySummary(0, null, "Account disconnected. No Audible library is currently connected.");
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
