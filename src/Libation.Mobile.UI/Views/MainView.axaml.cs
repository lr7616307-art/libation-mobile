using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Markup.Xaml;
using Libation.Mobile.Core;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.UI.Views;

public partial class MainView : UserControl
{
    private readonly MainViewModel _viewModel;
    private static readonly FilePickerFileType BackupFileType = new("Libation Mobile backup") { Patterns = ["*.json"] };

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

    private async void ExportBackupClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) return;
        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export catalogue backup",
            SuggestedFileName = "libation-mobile-backup.json",
            DefaultExtension = "json",
            FileTypeChoices = [BackupFileType]
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await _viewModel.ExportBackupAsync(stream);
    }

    private async void ImportBackupClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import catalogue backup",
            AllowMultiple = false,
            FileTypeFilter = [BackupFileType]
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        await _viewModel.PrepareImportAsync(stream);
    }

    private async void ConfirmImportClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await _viewModel.ConfirmImportAsync();

    private void CancelImportClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.CancelImport();

    private async void ReloadClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await _viewModel.ReloadAsync();

    private async void SyncLibraryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await _viewModel.SyncLibraryAsync();

    private void SortClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.ToggleSort();
    private void FilterClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.CycleCatalogueFilter();

    private async void AddBookClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await _viewModel.AddBookAsync();

    private void EditBookClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Audiobook book } && book.CanDelete)
            _viewModel.BeginEdit(book.Id);
    }

    private void CancelEditClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.CancelEdit();

    private async void DeleteBookClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Audiobook book } && book.CanDelete)
            await _viewModel.RemoveBookAsync(book.Id);
    }

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
