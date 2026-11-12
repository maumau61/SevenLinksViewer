using System.ComponentModel;

namespace SevenLinksViewer.Models;

/// <summary>
/// A day's worth of event folders, discovered without reading any .media contents.
/// The actual <see cref="EventClip"/>s (chunk lists + estimated durations) are built
/// lazily by <c>MediaCrawler.LoadClips</c> only when the user opens the day.
/// </summary>
public sealed class CameraDay : INotifyPropertyChanged
{
    public required DateTime Date { get; init; } // local date
    public required List<EventFolder> Folders { get; init; }

    public string DayLabel => Date.ToString("yyyy-MM-dd dddd");
    public string CountLabel => $"{Folders.Count} clip{(Folders.Count == 1 ? "" : "s")}";

    public void RaiseCountChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountLabel)));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A lightweight pointer to an event folder: its start time and path, no file I/O.</summary>
public sealed record EventFolder(DateTime StartUtc, string FolderPath);
