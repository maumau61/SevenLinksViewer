using System.IO;
using SevenLinksViewer.Models;

namespace SevenLinksViewer.Services;

public sealed class MediaCrawler
{
    public IReadOnlyList<EventClip> Crawl(string cameraRoot)
    {
        var dcim = Path.Combine(cameraRoot, "DCIM");
        var root = Directory.Exists(dcim) ? dcim : cameraRoot;
        if (!Directory.Exists(root)) return Array.Empty<EventClip>();

        return Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(TryParseEventFolder)
            .OfType<EventClip>()
            .OrderBy(e => e.StartUtc)
            .ToList();
    }

    private static EventClip? TryParseEventFolder(string folder)
    {
        var name = Path.GetFileName(folder);
        var underscore = name.IndexOf('_');
        var unixPart = underscore >= 0 ? name[..underscore] : name;
        if (!long.TryParse(unixPart, out var unixSeconds)) return null;
        if (unixSeconds < 1_000_000_000 || unixSeconds > 4_000_000_000) return null;

        var chunks = Directory.GetFiles(folder, "*.media")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (chunks.Length == 0) return null;

        return new EventClip
        {
            StartUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime,
            FolderPath = folder,
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
