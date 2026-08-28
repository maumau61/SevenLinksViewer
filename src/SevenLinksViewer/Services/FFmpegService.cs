using System.Diagnostics;
using System.IO;
using Xabe.FFmpeg.Downloader;

namespace SevenLinksViewer.Services;

public sealed class FFmpegService
{
    private readonly string _ffmpegDir;
    private string? _ffmpegExe;

    public FFmpegService(string ffmpegDir)
    {
        _ffmpegDir = ffmpegDir;
    }

    public bool IsReady => _ffmpegExe != null && File.Exists(_ffmpegExe);

    public async Task EnsureAvailableAsync(IProgress<string>? progress = null)
    {
        if (IsReady) return;
        Directory.CreateDirectory(_ffmpegDir);
        var candidate = Path.Combine(_ffmpegDir, "ffmpeg.exe");
        if (!File.Exists(candidate))
        {
            progress?.Report("Downloading ffmpeg (one-time, ~30 MB)\u2026");
            await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, _ffmpegDir);
            progress?.Report("ffmpeg ready.");
        }
        _ffmpegExe = candidate;
    }

    // Remux raw HEVC Annex-B into MP4 without re-encoding. We hand ffmpeg an explicit
    // -framerate so it can synthesize PTS for every NAL unit (raw HEVC carries no timestamps),
    // and tag the video as hvc1 so VLC/QuickTime/MediaFoundation all recognize it.
    public void RemuxHevcToMp4(string hevcPath, string mp4Path, double fps)
    {
        if (_ffmpegExe == null) throw new InvalidOperationException("ffmpeg not initialized; call EnsureAvailableAsync first.");
        if (File.Exists(mp4Path)) File.Delete(mp4Path);

        var psi = new ProcessStartInfo(_ffmpegExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-fflags");
        psi.ArgumentList.Add("+genpts");
        psi.ArgumentList.Add("-framerate");
        psi.ArgumentList.Add(fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(hevcPath);
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-tag:v");
        psi.ArgumentList.Add("hvc1");
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add(mp4Path);

        using var proc = Process.Start(psi) ?? throw new Exception("Failed to start ffmpeg.");
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new Exception($"ffmpeg failed (exit {proc.ExitCode}):\n{stderr}");
    }
}
