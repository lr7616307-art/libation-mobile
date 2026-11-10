using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.UI.Views;

/// <summary>
/// Small code-built panel so the shared UI project can host the Android
/// sign-in flow without taking a dependency on Android platform assemblies.
/// </summary>
internal static class AudibleSignInPanel
{
    public static Control Create(AudibleSignInViewModel viewModel)
    {
        var marketplace = new TextBox
        {
            PlaceholderText = "Audible marketplace country code, e.g. in, us, uk",
            Text = viewModel.Marketplace
        };
        marketplace.Bind(TextBox.TextProperty,
            new Binding(nameof(viewModel.Marketplace)) { Mode = BindingMode.TwoWay });

        var responseUrl = new TextBox
        {
            PlaceholderText = "Paste the final HTTPS address-bar URL here",
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 44
        };
        responseUrl.Bind(TextBox.TextProperty,
            new Binding(nameof(viewModel.ResponseUrl)) { Mode = BindingMode.TwoWay });

        var begin = new Button { Content = "Sign in with Audible" };
        begin.Bind(Button.IsEnabledProperty, new Binding(nameof(viewModel.CanBegin)));
        begin.Click += async (_, _) => await viewModel.BeginSignInAsync();

        var cancel = new Button { Content = "Cancel sign-in" };
        cancel.Bind(Button.IsEnabledProperty, new Binding(nameof(viewModel.CanCancel)));
        cancel.Click += async (_, _) => await viewModel.CancelSignInAsync();

        var complete = new Button { Content = "Complete sign-in" };
        complete.Bind(Button.IsEnabledProperty, new Binding(nameof(viewModel.CanComplete)));
        complete.Click += async (_, _) => await viewModel.CompleteSignInAsync();

        var disconnect = new Button { Content = "Disconnect Audible account" };
        disconnect.Bind(Button.IsEnabledProperty, new Binding(nameof(viewModel.CanBegin)));
        disconnect.Click += async (_, _) => await viewModel.DisconnectAsync();

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(viewModel.Status)));

        var content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Audible account", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Sign-in happens in your external browser. This app never asks for your Amazon password. The final browser URL contains temporary sign-in data: paste it only into this app and never send it to anyone.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.8
                },
                marketplace,
                begin,
                responseUrl,
                complete,
                cancel,
                disconnect,
                status
            }
        };

        var panel = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            Child = content,
            DataContext = viewModel
        };

        _ = viewModel.LoadStatusAsync();
        return panel;
    }
}
