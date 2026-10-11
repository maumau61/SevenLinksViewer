using System.IO;
using SevenLinksViewer.Models;

namespace SevenLinksViewer.Services;

public sealed class MediaConcatenator
{
    private const int RecordHeader = 24;

    // Audio records carry raw PCM: signed 16-bit little-endian, 8 kHz, mono (640 bytes per
    // 40 ms frame). Reverse-engineered with MediaProbe; see docs/media-format.md.
    public const int AudioSampleRate = 8000;
    public const int AudioChannels = 1;

    // HEVC End-of-Sequence NAL (type 36). Emitted between chunks so the decoder
    // flushes its DPB and treats the next IDR as the start of a fresh sequence.
    private static readonly byte[] EosNal = { 0x00, 0x00, 0x00, 0x01, 0x48, 0x00 };

    public PlayableHevc BuildPlayableHevc(EventClip clip, string cacheDir)
    {
        Directory.CreateDirectory(cacheDir);
        var safeName = $"{clip.StartUtc:yyyyMMdd_HHmmss}_{Path.GetFileName(clip.FolderPath)}.v13.h265";
        var outPath = Path.Combine(cacheDir, safeName);
        var framesPath = outPath + ".frames";
        var pcmPath = outPath + ".pcm";
        if (File.Exists(outPath) && new FileInfo(outPath).Length > 0 && File.Exists(framesPath))
            return new PlayableHevc(outPath, int.Parse(File.ReadAllText(framesPath)), ExistingAudio(pcmPath));

        var totalFrames = 0;
        long audioBytes = 0;
        using (var output = File.Create(outPath))
        using (var audio = File.Create(pcmPath))
        {
            for (var i = 0; i < clip.ChunkFiles.Count; i++)
            {
                var (frames, abytes) = WriteCleaned(clip.ChunkFiles[i], output, audio);
                totalFrames += frames;
                audioBytes += abytes;
                if (i < clip.ChunkFiles.Count - 1)
                    output.Write(EosNal, 0, EosNal.Length);
            }
        }

        // A 0-frame result means IsRecordHeader matched nothing — almost always a new
        // camera firmware that changed the record header. Don't cache the empty stub
        // (ffmpeg would report "hevc detected only with low score" and fail to play);
        // fail loudly so the cause is obvious instead of a confusing playback error.
        if (totalFrames == 0)
        {
            File.Delete(outPath);
            File.Delete(pcmPath);
            throw new InvalidDataException(
                $"No video frames found in '{Path.GetFileName(clip.FolderPath)}'. The .media record " +
                "header was not recognized — likely a new camera firmware. See MediaConcatenator.IsRecordHeader.");
        }

        // Some clips have no audio records; drop the empty PCM so we don't hand ffmpeg a
        // zero-length audio input.
        if (audioBytes == 0) File.Delete(pcmPath);

        File.WriteAllText(framesPath, totalFrames.ToString());
        return new PlayableHevc(outPath, totalFrames, audioBytes > 0 ? pcmPath : null);
    }

    private static string? ExistingAudio(string pcmPath) =>
        File.Exists(pcmPath) && new FileInfo(pcmPath).Length > 0 ? pcmPath : null;

    public readonly record struct PlayableHevc(string Path, int FrameCount, string? AudioPcmPath);

    // The .media file is a stream of [24-byte record header][payload] pairs.
    // Header layout:
    //   [00..03]  marker: 1 = IDR access unit (VPS+SPS+PPS+IDR), 0 = P-frame TRAIL
    //   [04..07]  payload length in bytes (LE u32)
    //   [08..11]  per-frame id (LE u32)
    //   [12]      flags: a per-firmware counter that only increases
    //             (0x91 on 2024, 0x9D on 2026/03-04, 0x9F on 2026/07, 0xA0 on 2026/08)
    //   [13..15]  literal 01 00 00
    //   [16..19]  presentation time, microseconds (LE u32)
    //   [20..23]  reserved zeros
    // Payload is `length` bytes. Video payloads (marker 0/1) are HEVC Annex-B starting with a
    // NAL start code; audio payloads (marker 3) are raw PCM. Everything else between records is
    // zero padding and is skipped. Video goes to `video`, PCM audio to `audio`.
    public static (int Frames, long AudioBytes) WriteCleaned(string mediaFile, Stream video, Stream audio)
    {
        var bytes = File.ReadAllBytes(mediaFile);
        var frames = 0;
        long audioBytes = 0;
        var pos = 0;
        while (pos + RecordHeader <= bytes.Length)
        {
            if (IsRecordHeader(bytes, pos))
            {
                var len = ReadLE32(bytes, pos + 4);
                if (len <= 0 || pos + RecordHeader + len > bytes.Length) { pos++; continue; }
                video.Write(bytes, pos + RecordHeader, len);
                frames++;
                pos += RecordHeader + len;
            }
            else if (IsAudioRecordHeader(bytes, pos))
            {
                var len = ReadLE32(bytes, pos + 4);
                if (len <= 0 || pos + RecordHeader + len > bytes.Length) { pos++; continue; }
                audio.Write(bytes, pos + RecordHeader, len);
                audioBytes += len;
                pos += RecordHeader + len;
            }
            else { pos++; }
        }
        return (frames, audioBytes);
    }

    private static bool IsRecordHeader(byte[] b, int i)
    {
        if (b[i + 1] != 0 || b[i + 2] != 0 || b[i + 3] != 0) return false;
        if (b[i] != 0 && b[i] != 1) return false;
        // Flag byte is a monotonically increasing per-firmware counter: 0x91 (2024) ->
        // 0x9D (2026/03) -> 0x9F (2026/07) -> 0xA0 (2026/08, rolled past the 0x9 nibble).
        // Don't anchor on the high nibble (that broke on 0xA0); just require a sane floor
        // and let the other constraints below carry the detection, so future bumps don't
        // reject every record (which yields 0 frames -> unplayable clip).
        if (b[i + 12] < 0x90) return false;
        if (b[i + 13] != 0x01 || b[i + 14] != 0 || b[i + 15] != 0) return false;
        if (b[i + 20] != 0 || b[i + 21] != 0 || b[i + 22] != 0 || b[i + 23] != 0) return false;
        // Payload must start with HEVC Annex-B start code.
        if (i + RecordHeader + 4 > b.Length) return false;
        if (b[i + RecordHeader] != 0 || b[i + RecordHeader + 1] != 0 ||
            b[i + RecordHeader + 2] != 0 || b[i + RecordHeader + 3] != 1) return false;
        return true;
    }

    // Audio records share the 24-byte framing but use marker 3 (vs 0/1 for video) and carry raw
    // PCM, so there's no HEVC start code to anchor on — the fixed header fields do the detection.
    private static bool IsAudioRecordHeader(byte[] b, int i)
    {
        if (b[i] != 3 || b[i + 1] != 0 || b[i + 2] != 0 || b[i + 3] != 0) return false;
        if (b[i + 12] < 0x90) return false;
        if (b[i + 13] != 0x01 || b[i + 14] != 0 || b[i + 15] != 0) return false;
        return b[i + 20] == 0 && b[i + 21] == 0 && b[i + 22] == 0 && b[i + 23] == 0;
    }

    private static int ReadLE32(byte[] b, int i) => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);
}
