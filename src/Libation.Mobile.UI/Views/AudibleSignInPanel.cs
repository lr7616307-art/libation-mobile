using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.UI.Views;

/// <summary>
/// Renders one account summary tile. The tile is deliberately the same horizontal
/// card shape as an Audible library entry so the account area can become a list
/// when secure multi-account storage is added.
/// </summary>
internal static class AudibleSignInPanel
{
    public static Control Create(
        AudibleSignInViewModel viewModel,
        Action addAccount,
        Func<Task> syncLibrary)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(addAccount);
        ArgumentNullException.ThrowIfNull(syncLibrary);

        var name = new TextBlock { FontSize = 16, FontWeight = FontWeight.Bold };
        name.Bind(TextBlock.TextProperty, new Binding(nameof(viewModel.AccountDisplayName)));

        var connection = new TextBlock { FontSize = 12, Foreground = Brushes.LightGreen };
        connection.Bind(TextBlock.TextProperty, new Binding(nameof(viewModel.ConnectionStatusLabel)));

        var accountId = new TextBlock { FontSize = 11, Opacity = 0.75 };
        accountId.Bind(TextBlock.TextProperty, new Binding(nameof(viewModel.AccountIdLabel)));

        var marketplace = new TextBlock { FontSize = 11, Opacity = 0.75 };
        marketplace.Bind(TextBlock.TextProperty, new Binding(nameof(viewModel.MarketplaceLabel)));

        var count = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold };
        count.Bind(TextBlock.TextProperty, new Binding(nameof(viewModel.SyncedAudiobookCountLabel)));

        var lastSync = new TextBlock { FontSize = 10, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        lastSync.Bind(TextBlock.TextProperty, new Binding(nameof(viewModel.LastLibrarySyncLabel)));

        var syncStatus = new TextBlock { FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        syncStatus.Bind(TextBlock.TextProperty, new Binding(nameof(viewModel.LibrarySyncStatusLabel)));

        var card = new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8),
            Background = new SolidColorBrush(Color.Parse("#171321")),
            BorderBrush = new SolidColorBrush(Color.Parse("#51416F")),
            BorderThickness = new Thickness(1),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("78,*,Auto"),
                Children =
                {
                    new Border
                    {
                        Width = 68,
                        Height = 92,
                        CornerRadius = new CornerRadius(8),
                        Background = new SolidColorBrush(Color.Parse("#292238")),
                        Child = new TextBlock
                        {
                            Text = "◉",
                            FontSize = 34,
                            Foreground = new SolidColorBrush(Color.Parse("#B9A5E8")),
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                        }
                    },
                    new StackPanel
                    {
                        [Grid.ColumnProperty] = 1,
                        Margin = new Thickness(12, 0),
                        Spacing = 4,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Children = { name, connection, accountId, marketplace, count, lastSync, syncStatus }
                    },
                    new TextBlock
                    {
                        [Grid.ColumnProperty] = 2,
                        Text = "AUDIBLE",
                        FontSize = 9,
                        LetterSpacing = 1,
                        Foreground = new SolidColorBrush(Color.Parse("#B9A5E8")),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    }
                }
            }
        };

        var add = new Button
        {
            Content = "Add Account",
            MinHeight = 54,
            FontSize = 17,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Color.Parse("#174B55"))
        };
        add.Bind(Button.IsEnabledProperty, new Binding(nameof(viewModel.CanAddAccount)));
        add.Click += (_, _) => addAccount();

        var refreshRegistration = new Button
        {
            Content = "Refresh Android registration",
            MinHeight = 46,
            Background = new SolidColorBrush(Color.Parse("#174B55"))
        };
        refreshRegistration.Bind(Button.IsVisibleProperty,
            new Binding(nameof(viewModel.RequiresAndroidRegistrationRefresh)));
        refreshRegistration.Bind(Button.IsEnabledProperty,
            new Binding(nameof(viewModel.CanRefreshAndroidRegistration)));
        refreshRegistration.Click += async (_, _) => await viewModel.BeginSignInAsync();

        var refreshRegistrationNote = new TextBlock
        {
            Text = "Use this once when VØRATH reports an outdated Audible device profile. It refreshes the account registration; it is not an approval request for each audiobook.",
            FontSize = 11,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap
        };
        refreshRegistrationNote.Bind(TextBlock.IsVisibleProperty,
            new Binding(nameof(viewModel.RequiresAndroidRegistrationRefresh)));

        var multiAccountNotice = new TextBlock
        {
            Text = "This build currently supports one active Audible account. Multi-account profiles and switching are not enabled yet.",
            FontSize = 11,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        };
        multiAccountNotice.Bind(TextBlock.IsVisibleProperty, new Binding(nameof(viewModel.HasConnectedAccount)));

        var sync = new Button { Content = "⟳  Sync Audible library", MinHeight = 46 };
        sync.Bind(Button.IsEnabledProperty, new Binding(nameof(viewModel.CanSyncLibrary)));
        sync.Click += async (_, _) => await syncLibrary();

        var disconnect = new Button
        {
            Content = "Disconnect this account",
            MinHeight = 44,
            Foreground = Brushes.IndianRed
        };
        disconnect.Bind(Button.IsEnabledProperty, new Binding(nameof(viewModel.CanBegin)));
        disconnect.Bind(Button.IsVisibleProperty, new Binding(nameof(viewModel.HasConnectedAccount)));
        disconnect.Click += async (_, _) => await viewModel.DisconnectAsync();

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(viewModel.Status)));

        var content = new StackPanel
        {
            Spacing = 10,
            Children = { card, add, refreshRegistration, refreshRegistrationNote, multiAccountNotice, sync, disconnect, status }
        };

        var panel = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(0),
            Child = content,
            DataContext = viewModel
        };

        _ = viewModel.LoadStatusAsync();
        return panel;
    }
}
