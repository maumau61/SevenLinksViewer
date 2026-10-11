using System.IO;
using SevenLinksViewer.Models;

namespace SevenLinksViewer.Services;

public sealed class MediaCrawler
{
    /// <summary>
    /// First pass: enumerate every event folder and group it into days WITHOUT reading any
    /// .media file contents. This keeps startup cheap and memory small for large cards — the
    /// per-clip chunk lists and duration estimates are deferred to <see cref="LoadClips"/>,
    /// which only runs when the user opens a day.
    /// </summary>
    public IReadOnlyList<CameraDay> CrawlDays(string cameraRoot)
    {
        var dcim = Path.Combine(cameraRoot, "DCIM");
        var root = Directory.Exists(dcim) ? dcim : cameraRoot;
        if (!Directory.Exists(root)) return Array.Empty<CameraDay>();

        return Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(TryParseEventFolder)
            .OfType<EventFolder>()
            .GroupBy(f => f.StartUtc.ToLocalTime().Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new CameraDay
            {
                Date = g.Key,
                Folders = g.OrderByDescending(f => f.StartUtc).ToList()
            })
            .ToList();
    }

    /// <summary>
    /// Second pass: build the playable clips for a single day. This reads each folder's .media
    /// chunk list and estimates durations (opening chunk headers), so the file I/O is scoped to
    /// the one day the user actually opened rather than the whole card.
    /// </summary>
    public IReadOnlyList<EventClip> LoadClips(CameraDay day) =>
        day.Folders
            .Select(BuildClip)
            .OfType<EventClip>()
            .OrderByDescending(e => e.StartUtc)
            .ToList();

    private static EventFolder? TryParseEventFolder(string folder)
    {
        var name = Path.GetFileName(folder);
        var underscore = name.IndexOf('_');
        var unixPart = underscore >= 0 ? name[..underscore] : name;
        if (!long.TryParse(unixPart, out var unixSeconds)) return null;
        if (unixSeconds < 1_000_000_000 || unixSeconds > 4_000_000_000) return null;

        // Cheap existence check so empty folders don't show up as days — but don't
        // materialize the chunk list or open any file yet.
        if (!Directory.EnumerateFiles(folder, "*.media").Any()) return null;

        return new EventFolder(
            DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime,
            folder);
    }

    private static EventClip? BuildClip(EventFolder folder)
    {
        var chunks = Directory.GetFiles(folder.FolderPath, "*.media")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (chunks.Length == 0) return null;

        return new EventClip
        {
            StartUtc = folder.StartUtc,
            FolderPath = folder.FolderPath,
            ChunkFiles = chunks,
            EstimatedDuration = EstimateDuration(chunks)
        };
    }

    private static TimeSpan EstimateDuration(string[] chunks)
    {
        if (chunks.Length == 0) return TimeSpan.Zero;
        if (chunks.Length == 1) return TimeSpan.FromSeconds(12);

        // dts in chunk headers is absolute (camera-session) time, so subtract the first
        // chunk's dts to make the duration clip-relative, then add one chunk-duration to
        // cover the last chunk's content.
        var firstDtsUs = ReadDtsHeaderMicros(chunks[0]);
        var lastDtsUs = ReadDtsHeaderMicros(chunks[^1]);
        var prevDtsUs = ReadDtsHeaderMicros(chunks[^2]);
        var perChunkUs = Math.Max(lastDtsUs - prevDtsUs, 1_000_000);
        return TimeSpan.FromMilliseconds((lastDtsUs - firstDtsUs + perChunkUs) / 1000.0);
    }

    private static long ReadDtsHeaderMicros(string mediaFile)
    {
        try
        {
            using var fs = File.OpenRead(mediaFile);
            Span<byte> header = stackalloc byte[24];
            return fs.Read(header) < 24 ? 0 : BitConverter.ToUInt32(header[16..20]);
        }
        catch
        {
            return 0;
        }
    }
}
