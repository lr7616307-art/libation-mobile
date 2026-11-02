using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Libation.Mobile.Core;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.UI.Views;

public partial class MainView : UserControl
{
    private readonly MainViewModel _viewModel;

    public MainView()
    {
        var dataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _viewModel = new MainViewModel(
            new JsonLibraryRepository(Path.Combine(dataDirectory, "library.json")),
            new JsonDownloadQueue(Path.Combine(dataDirectory, "download-queue.json")));
        InitializeComponent();
        DataContext = _viewModel;
        _ = _viewModel.InitializeAsync();
    }

    public MainView(ILibraryRepository repository, IDownloadQueue downloadQueue)
    {
        _viewModel = new MainViewModel(repository, downloadQueue);
        InitializeComponent();
        DataContext = _viewModel;
        _ = _viewModel.InitializeAsync();
    }

    private async void ReloadClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await _viewModel.ReloadAsync();

    private async void AddBookClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await _viewModel.AddBookAsync();

    private async void QueueClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Audiobook book })
            await _viewModel.EnqueueAsync(book.Id);
    }

    private async void RetryQueueClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DownloadQueueItem item } && item.Status == DownloadStatus.Failed)
            await _viewModel.RetryFailedAsync(item.AudiobookId);
    }

    private async void RemoveQueueClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DownloadQueueItem item })
            await _viewModel.RemoveFromQueueAsync(item.AudiobookId);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
