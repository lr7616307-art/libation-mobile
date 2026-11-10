using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.UI.Views;

/// <summary>Compact account controls; the provider login itself opens in its own in-app page.</summary>
internal static class AudibleSignInPanel
{
    public static Control Create(AudibleSignInViewModel viewModel)
    {
        var begin = new Button { Content = "Sign in with Audible" };
        begin.Bind(Button.IsEnabledProperty, new Binding(nameof(viewModel.CanBegin)));
        begin.Click += async (_, _) => await viewModel.BeginSignInAsync();

        var disconnect = new Button { Content = "Disconnect Audible account" };
        disconnect.Bind(Button.IsEnabledProperty, new Binding(nameof(viewModel.CanBegin)));
        disconnect.Click += async (_, _) => await viewModel.DisconnectAsync();

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(viewModel.Status)));

        var content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Audible account", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Sign in to Amazon India in the in-app Audible page. Your password and OTP stay on Amazon's page; VØRATH only receives the final sign-in response.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.8
                },
                begin,
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
