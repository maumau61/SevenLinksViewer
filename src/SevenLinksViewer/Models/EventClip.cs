namespace SevenLinksViewer.Models;

public sealed class EventClip
{
    public required DateTime StartUtc { get; init; }
    public required string FolderPath { get; init; }
    public required IReadOnlyList<string> ChunkFiles { get; init; }
    public required TimeSpan EstimatedDuration { get; init; }

    public DateTime StartLocal => StartUtc.ToLocalTime();

    public string DayLabel => StartLocal.ToString("yyyy-MM-dd dddd");
    public string TimeLabel => StartLocal.ToString("HH:mm:ss");
    public string DetailLabel => $"{FormatDuration(EstimatedDuration)} \u2022 {ChunkFiles.Count} chunk{(ChunkFiles.Count == 1 ? "" : "s")}";

    private static string FormatDuration(TimeSpan d) =>
        d.TotalMinutes >= 1
            ? $"{(int)d.TotalMinutes}m {d.Seconds:00}s"
            : $"{d.TotalSeconds:0.0}s";
}
