using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Libation.Mobile.Core;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.UI.Views;

public partial class MainView : UserControl
{
    private readonly MainViewModel _viewModel;

    public MainView(ILibraryRepository repository)
    {
        InitializeComponent();
        _viewModel = new MainViewModel(repository);
        DataContext = _viewModel;
        _ = _viewModel.InitializeAsync();
    }

    private async void ReloadClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await _viewModel.ReloadAsync();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
