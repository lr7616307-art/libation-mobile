using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Libation.Mobile.Core;

namespace Libation.Mobile.UI.ViewModels;

/// <summary>Demo-only view model. Replace sample data with repository integration.</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly List<Audiobook> _allBooks =
    [
        new("demo-001", "The Long Way to a Small, Angry Planet", "Becky Chambers", "Rachel Dulude", "12h 20m", "Sample"),
        new("demo-002", "Project Hail Mary", "Andy Weir", "Ray Porter", "16h 10m", "Sample"),
        new("demo-003", "The Three-Body Problem", "Cixin Liu", "Luke Daniels", "13h 40m", "Sample")
    ];

    private string _searchText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<Audiobook> Books { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public MainViewModel() => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? _allBooks
            : _allBooks.Where(book =>
                book.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                book.Author.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                book.Narrator.Contains(query, StringComparison.OrdinalIgnoreCase));

        Books.Clear();
        foreach (var book in matches)
            Books.Add(book);

        OnPropertyChanged(nameof(Books));
        OnPropertyChanged(nameof(HasBooks));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    public bool HasBooks => Books.Count > 0;
    public string EmptyMessage => HasBooks ? string.Empty : "No matching sample audiobooks.";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
