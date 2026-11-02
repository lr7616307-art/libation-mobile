using System.Collections.ObjectModel;
using Libation.Mobile.Core;

namespace Libation.Mobile.UI.ViewModels;

/// <summary>Demo-only view model. Replace sample data with repository integration.</summary>
public sealed class MainViewModel
{
    public ObservableCollection<Audiobook> Books { get; } =
    [
        new("demo-001", "The Long Way to a Small, Angry Planet", "Becky Chambers",
            "Rachel Dulude", "12h 20m", "Sample"),
        new("demo-002", "Project Hail Mary", "Andy Weir",
            "Ray Porter", "16h 10m", "Sample"),
        new("demo-003", "The Three-Body Problem", "Cixin Liu",
            "Luke Daniels", "13h 40m", "Sample")
    ];
}
