using System.Text;

if (args.Length < 1)
{
    Console.WriteLine("Usage: MediaProbe <path-to-.media-file-or-event-folder> [--extract <output.h265>] [--clean-extract <cleaned.h265>] [--dump-audio <audio.bin>]");
    return 1;
}

var input = args[0];
string? extractPath = null;
string? cleanExtractPath = null;
string? dumpAudioPath = null;
for (var i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--extract") extractPath = args[i + 1];
    if (args[i] == "--clean-extract") cleanExtractPath = args[i + 1];
    if (args[i] == "--dump-audio") dumpAudioPath = args[i + 1];
}

var files = File.Exists(input)
    ? new[] { input }
    : Directory.GetFiles(input, "*.media").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();

if (files.Length == 0)
{
    Console.Error.WriteLine($"No .media files found at {input}");
    return 1;
}

Console.WriteLine($"Found {files.Length} .media file(s)");

FileStream? extractStream = null;
FileStream? cleanStream = null;
if (extractPath != null)
{
    extractStream = File.Create(extractPath);
    Console.WriteLine($"Extracting raw HEVC bitstream to {extractPath}");
}
if (cleanExtractPath != null)
{
    cleanStream = File.Create(cleanExtractPath);
    Console.WriteLine($"Extracting CLEANED HEVC bitstream (per-GOP headers stripped) to {cleanExtractPath}");
}
FileStream? audioStream = null;
if (dumpAudioPath != null)
{
    audioStream = File.Create(dumpAudioPath);
    Console.WriteLine($"Dumping non-video (audio + padding, zero-trimmed) bytes to {dumpAudioPath}");
}

var totalNalCounts = new Dictionary<int, int>();

foreach (var file in files)
{
    Console.WriteLine();
    Console.WriteLine($"=== {Path.GetFileName(file)} ({new FileInfo(file).Length:N0} bytes) ===");
    var bytes = File.ReadAllBytes(file);
    DumpHeader(bytes);
    var nalCounts = ScanNalUnits(bytes, extractStream);
    ScanLooseHeaders(bytes);
    if (cleanStream != null) WriteCleaned(bytes, cleanStream);
    AnalyzeNalsBetweenHeaders(bytes);
    AnalyzeChunkTail(bytes);
    SearchFakeStartCodes(bytes);
    AnalyzeAudio(bytes, audioStream);
    foreach (var kv in nalCounts)
    {
        totalNalCounts[kv.Key] = totalNalCounts.GetValueOrDefault(kv.Key) + kv.Value;
    }
}

extractStream?.Dispose();
cleanStream?.Dispose();
audioStream?.Dispose();

Console.WriteLine();
Console.WriteLine("=== TOTAL NAL UNIT SUMMARY ===");
foreach (var kv in totalNalCounts.OrderBy(kv => kv.Key))
{
    Console.WriteLine($"  type {kv.Key,2} ({HevcNalName(kv.Key)}): {kv.Value}");
}

return 0;

static void DumpHeader(byte[] bytes)
{
    Console.WriteLine("First 32 bytes (hex):");
    var sb = new StringBuilder();
    for (var i = 0; i < Math.Min(32, bytes.Length); i++)
    {
        sb.Append(bytes[i].ToString("X2"));
        sb.Append(' ');
        if ((i + 1) % 16 == 0) sb.AppendLine();
    }
    Console.WriteLine(sb.ToString().TrimEnd());

    if (bytes.Length >= 24)
    {
        Console.WriteLine("Header field guesses (24-byte header hypothesis):");
        Console.WriteLine($"  [00..03] LE u32 = {BitConverter.ToUInt32(bytes, 0)} (version/marker?)");
        Console.WriteLine($"  [04..07] LE u32 = {BitConverter.ToUInt32(bytes, 4)} (length/count?)");
        Console.WriteLine($"  [08..11] LE u32 = {BitConverter.ToUInt32(bytes, 8)} (id/random?)");
        Console.WriteLine($"  [12..15] LE u32 = {BitConverter.ToUInt32(bytes, 12)} (pts?)");
        Console.WriteLine($"  [16..19] LE u32 = {BitConverter.ToUInt32(bytes, 16)} (dts/duration?)");
        Console.WriteLine($"  [20..23] LE u32 = {BitConverter.ToUInt32(bytes, 20)} (reserved?)");
    }

    Console.WriteLine();
    Console.WriteLine("Last 64 bytes (hex) - looking for trailing footer/incomplete NAL:");
    var tailStart = Math.Max(0, bytes.Length - 64);
    var tail = new StringBuilder();
    for (var i = tailStart; i < bytes.Length; i++)
    {
        tail.Append(bytes[i].ToString("X2"));
        tail.Append(' ');
        if ((i - tailStart + 1) % 16 == 0) tail.AppendLine();
    }
    Console.WriteLine(tail.ToString().TrimEnd());
}

static Dictionary<int, int> ScanNalUnits(byte[] bytes, FileStream? extractStream)
{
    var counts = new Dictionary<int, int>();
    var nals = new List<(int Offset, int Type, int StartCodeLen)>();
    for (var i = 0; i < bytes.Length - 4; i++)
    {
        var isFour = bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 0 && bytes[i + 3] == 1;
        var isThree = !isFour && bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1;
        if (!isFour && !isThree) continue;
        var startCodeLen = isFour ? 4 : 3;
        var nalHeaderOffset = i + startCodeLen;
        if (nalHeaderOffset >= bytes.Length) break;
        var nalType = (bytes[nalHeaderOffset] >> 1) & 0x3F;
        counts[nalType] = counts.GetValueOrDefault(nalType) + 1;
        nals.Add((i, nalType, startCodeLen));
        i = nalHeaderOffset;
    }

    Console.WriteLine($"NAL units found: {nals.Count}");
    if (nals.Count > 0)
    {
        Console.WriteLine($"  first start code at offset 0x{nals[0].Offset:X}");
        var lastNal = nals[^1];
        var lastNalEnd = lastNal.Offset + lastNal.StartCodeLen + 2;
        Console.WriteLine($"  LAST start code at offset 0x{lastNal.Offset:X} (NAL type {lastNal.Type} {HevcNalName(lastNal.Type)})");
        Console.WriteLine($"  bytes from last NAL header to EOF: {bytes.Length - lastNalEnd}");
    }
    foreach (var kv in counts.OrderBy(kv => kv.Key))
        Console.WriteLine($"  type {kv.Key,2} ({HevcNalName(kv.Key)}): {kv.Value}");

    // Inspect bytes BEFORE each VPS (parameter set boundary = GOP boundary)
    Console.WriteLine();
    Console.WriteLine("Bytes preceding each VPS (looking for per-GOP headers):");
    var vpsCount = 0;
    for (var k = 0; k < nals.Count; k++)
    {
        if (nals[k].Type != 32) continue;
        vpsCount++;
        var vpsStart = nals[k].Offset;
        if (k == 0)
        {
            Console.WriteLine($"  VPS #{vpsCount} @ 0x{vpsStart:X}: <file start, header is bytes 0..{vpsStart - 1}>");
            continue;
        }
        var prevNal = nals[k - 1];
        var prevNalEnd = FindNalEnd(bytes, prevNal.Offset + prevNal.StartCodeLen, vpsStart);
        var gap = vpsStart - prevNalEnd;
        var preview = HexPreview(bytes, Math.Max(0, vpsStart - 32), Math.Min(bytes.Length, vpsStart + 5));
        Console.WriteLine($"  VPS #{vpsCount} @ 0x{vpsStart:X}: gap from prev NAL end = {gap} bytes, type before = {prevNal.Type}");
        Console.WriteLine($"    bytes [0x{Math.Max(0, vpsStart - 32):X}..0x{vpsStart + 4:X}]: {preview}");
    }

    if (extractStream != null && nals.Count > 0)
    {
        var firstStart = nals[0].Offset;
        extractStream.Write(bytes, firstStart, bytes.Length - firstStart);
    }
    return counts;
}

static int FindNalEnd(byte[] bytes, int searchStart, int hardStop)
{
    // Find the byte just past the last non-zero byte before the next start code
    var end = hardStop - 1;
    while (end > searchStart && bytes[end] == 0) end--;
    return end + 1;
}

static string HexPreview(byte[] bytes, int start, int endExclusive)
{
    var sb = new StringBuilder();
    for (var i = start; i < endExclusive; i++)
    {
        sb.Append(bytes[i].ToString("X2"));
        sb.Append(' ');
    }
    return sb.ToString().TrimEnd();
}

static void AnalyzeChunkTail(byte[] bytes)
{
    Console.WriteLine();
    Console.WriteLine("=== Tail analysis: Where does last NAL's slice data REALLY end? ===");
    // Find last NAL start code
    var lastNalStart = -1;
    for (var i = bytes.Length - 5; i >= 0; i--)
    {
        if (bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 0 && bytes[i + 3] == 1) { lastNalStart = i; break; }
        if (bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1) { lastNalStart = i; break; }
    }
    if (lastNalStart < 0) { Console.WriteLine("  no NAL start code found"); return; }
    Console.WriteLine($"  last NAL start at 0x{lastNalStart:X}, file ends at 0x{bytes.Length:X}");
    Console.WriteLine($"  bytes after last NAL header: {bytes.Length - lastNalStart}");
    // Show last 32 bytes in detail
    Console.WriteLine("  last 32 bytes:");
    for (var i = Math.Max(0, bytes.Length - 32); i < bytes.Length; i += 16)
    {
        var sb = new StringBuilder();
        for (var j = i; j < Math.Min(i + 16, bytes.Length); j++) { sb.Append(bytes[j].ToString("X2")); sb.Append(' '); }
        Console.WriteLine($"    0x{i:X}: {sb}");
    }
    // Look for last byte that's "video-like" — work backward, find first non-zero non-FF byte
    var trailing = 0;
    for (var i = bytes.Length - 1; i > lastNalStart; i--)
    {
        if (bytes[i] != 0 && bytes[i] != 0xFF) { trailing = bytes.Length - i - 1; break; }
    }
    Console.WriteLine($"  trailing zero/0xFF bytes at end: {trailing}");
}

static void AnalyzeNalsBetweenHeaders(byte[] bytes)
{
    Console.WriteLine();
    Console.WriteLine("=== NAL distribution between per-GOP headers ===");
    var headers = new List<int>();
    if (IsGopHeaderStrict(bytes, 0)) headers.Add(0);
    var p = 24;
    while (p < bytes.Length)
    {
        var n = FindNextGopHeader(bytes, p);
        if (n < 0) break;
        headers.Add(n);
        p = n + 24;
    }
    headers.Add(bytes.Length);
    for (var k = 0; k < headers.Count - 1; k++)
    {
        var s = headers[k] + 24;
        var e = headers[k + 1];
        var videoLen = headers[k] + 4 < bytes.Length ? (int)BitConverter.ToUInt32(bytes, headers[k] + 4) : 0;
        var nalsByType = new Dictionary<int, int>();
        for (var i = s; i < e - 4; i++)
        {
            var isFour = bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 0 && bytes[i + 3] == 1;
            var isThree = !isFour && bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1;
            if (!isFour && !isThree) continue;
            var off = i + (isFour ? 4 : 3);
            var t = (bytes[off] >> 1) & 0x3F;
            nalsByType[t] = nalsByType.GetValueOrDefault(t) + 1;
            i = off;
        }
        Console.WriteLine($"  GOP {k + 1}: header@0x{headers[k]:X} videoLen={videoLen} span=0x{s:X}..0x{e:X} ({e - s} bytes)");
        Console.Write("    NALs:");
        foreach (var kv in nalsByType.OrderBy(kv => kv.Key)) Console.Write($" t{kv.Key}={kv.Value}");
        Console.WriteLine();
        // Show NAL positions to understand layout
        var nalPositions = new List<(int Off, int Type)>();
        for (var i = s; i < e - 4; i++)
        {
            var isFour = bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 0 && bytes[i + 3] == 1;
            var isThree = !isFour && bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1;
            if (!isFour && !isThree) continue;
            var off = i + (isFour ? 4 : 3);
            nalPositions.Add((i, (bytes[off] >> 1) & 0x3F));
            i = off;
        }
        if (nalPositions.Count > 0)
        {
            var lastBeforeCut = nalPositions.LastOrDefault(p2 => p2.Off < s + videoLen);
            var firstAfterCut = nalPositions.FirstOrDefault(p2 => p2.Off >= s + videoLen);
            Console.WriteLine($"    cut at 0x{s + videoLen:X}: last NAL before cut = 0x{lastBeforeCut.Off:X} t{lastBeforeCut.Type}, first NAL after cut = 0x{firstAfterCut.Off:X} t{firstAfterCut.Type}");
        }
    }
}

static void SearchFakeStartCodes(byte[] bytes)
{
    Console.WriteLine();
    Console.WriteLine("=== Search for fake start codes in audio gaps ===");
    var headers = new List<int>();
    if (IsGopHeaderStrict(bytes, 0)) headers.Add(0);
    var p = 24;
    while (p < bytes.Length)
    {
        var n = FindNextGopHeader(bytes, p);
        if (n < 0) break;
        headers.Add(n);
        p = n + 24;
    }

    // Find audio gaps: from last NAL of each GOP to next per-GOP header (or EOF for last)
    for (var k = 0; k < headers.Count; k++)
    {
        var s = headers[k] + 24;
        var e = k + 1 < headers.Count ? headers[k + 1] : bytes.Length;
        // Find last NAL start in this region
        var lastNal = -1;
        for (var i = s; i < e - 4; i++)
        {
            var isFour = bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 0 && bytes[i + 3] == 1;
            var isThree = !isFour && bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1;
            if (isFour || isThree) lastNal = i;
        }
        if (lastNal < 0) continue;
        // Audio gap: from approximate end of last NAL to e
        // We don't know exact end, so scan from lastNal+8 onward looking for fake start codes
        var gapStart = lastNal + 8;
        var fakes = new List<(int Off, int Type)>();
        for (var i = gapStart; i < e - 4; i++)
        {
            var isFour = bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 0 && bytes[i + 3] == 1;
            var isThree = !isFour && bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1;
            if (!isFour && !isThree) continue;
            var headerOff = i + (isFour ? 4 : 3);
            if (headerOff >= bytes.Length) break;
            var nt = (bytes[headerOff] >> 1) & 0x3F;
            fakes.Add((i, nt));
            i = headerOff;
        }
        Console.WriteLine($"  GOP {k + 1} tail [0x{lastNal:X}+..0x{e:X}] ({e - lastNal} bytes): {fakes.Count} fake start codes");
        foreach (var (off, nt) in fakes.Take(8)) Console.WriteLine($"    fake @ 0x{off:X}: NAL type {nt} ({HevcNalName(nt)})");
    }
}

static bool IsGopHeaderStrict(byte[] b, int i) =>
    i + 24 <= b.Length
    && b[i] == 0x01 && b[i + 1] == 0 && b[i + 2] == 0 && b[i + 3] == 0
    && b[i + 13] == 0x01 && b[i + 14] == 0 && b[i + 15] == 0
    && b[i + 20] == 0 && b[i + 21] == 0 && b[i + 22] == 0 && b[i + 23] == 0;

static void WriteCleaned(byte[] bytes, Stream output)
{
    const int headerBytes = 24;
    var pos = headerBytes;
    while (pos < bytes.Length)
    {
        var nextHeader = FindNextGopHeader(bytes, pos);
        var end = nextHeader < 0 ? bytes.Length : nextHeader;
        output.Write(bytes, pos, end - pos);
        if (nextHeader < 0) break;
        pos = nextHeader + headerBytes;
    }
}

static int FindNextGopHeader(byte[] b, int from)
{
    var max = b.Length - 29;
    for (var i = from; i <= max; i++)
    {
        if (b[i] != 0x01 || b[i + 1] != 0 || b[i + 2] != 0 || b[i + 3] != 0) continue;
        if (b[i + 13] != 0x01 || b[i + 14] != 0 || b[i + 15] != 0) continue;
        if (b[i + 20] != 0 || b[i + 21] != 0 || b[i + 22] != 0 || b[i + 23] != 0) continue;
        if (b[i + 24] != 0 || b[i + 25] != 0 || b[i + 26] != 0 || b[i + 27] != 1) continue;
        if (b[i + 28] != 0x40) continue;
        return i;
    }
    return -1;
}

static void ScanLooseHeaders(byte[] b)
{
    Console.WriteLine();
    Console.WriteLine("=== LOOSE per-GOP header scan (matching MediaConcatenator signature) ===");
    var max = b.Length - 29;
    var hits = 0;
    for (var i = 24; i <= max; i++)
    {
        if (b[i] != 0x01 || b[i + 1] != 0 || b[i + 2] != 0 || b[i + 3] != 0) continue;
        if (b[i + 13] != 0x01 || b[i + 14] != 0 || b[i + 15] != 0) continue;
        if (b[i + 20] != 0 || b[i + 21] != 0 || b[i + 22] != 0 || b[i + 23] != 0) continue;
        if (b[i + 24] != 0 || b[i + 25] != 0 || b[i + 26] != 0 || b[i + 27] != 1) continue;
        if (b[i + 28] != 0x40) continue;
        Console.WriteLine($"  hit @ 0x{i:X}: byte[12]=0x{b[i + 12]:X2}");
        hits++;
    }
    Console.WriteLine($"Total hits: {hits}");
}

// Isolate the non-video bytes. We walk the VIDEO records exactly the way the app's
// MediaConcatenator does (anchored on the 24-byte header + HEVC start code), so every byte
// NOT consumed by a video record is "non-video" = audio fragments + zero padding. That makes
// no assumption about the audio's own structure — it's purely the complement of the video.
// Then we try to interpret each non-video region as records and look for codec signatures so
// the audio format can be reverse-engineered.
static void AnalyzeAudio(byte[] bytes, FileStream? audioStream)
{
    Console.WriteLine();
    Console.WriteLine("=== Audio / non-video byte analysis ===");

    // 1. Map the video records (same logic as MediaConcatenator.IsRecordHeader).
    var videoSpans = new List<(int Start, int End)>();
    var pos = 0;
    while (pos + 24 <= bytes.Length)
    {
        if (!IsVideoRecordHeader(bytes, pos)) { pos++; continue; }
        var len = ReadLE32(bytes, pos + 4);
        if (len <= 0 || pos + 24 + len > bytes.Length) { pos++; continue; }
        videoSpans.Add((pos, pos + 24 + len));
        pos += 24 + len;
    }

    // 2. The complement of the video spans is the non-video data.
    var gaps = new List<(int Start, int End)>();
    var cursor = 0;
    foreach (var (s, e) in videoSpans)
    {
        if (s > cursor) gaps.Add((cursor, s));
        cursor = e;
    }
    if (cursor < bytes.Length) gaps.Add((cursor, bytes.Length));

    long totalGap = 0, totalNonZero = 0;
    foreach (var (s, e) in gaps)
    {
        totalGap += e - s;
        for (var i = s; i < e; i++) if (bytes[i] != 0) totalNonZero++;
    }

    Console.WriteLine($"  video records: {videoSpans.Count}, non-video regions: {gaps.Count}");
    Console.WriteLine($"  non-video bytes: {totalGap:N0} total, {totalNonZero:N0} non-zero, {totalGap - totalNonZero:N0} zero padding");

    // 3. Inspect each non-video region: trim pure padding, show a hex/ASCII preview, probe for
    //    an embedded record header and common audio codec signatures, and dump the content.
    var recordLengths = new List<int>();
    var recordPts = new List<uint>();
    var shown = 0;
    var adtsSeen = false;
    foreach (var (s, e) in gaps)
    {
        var cs = s; while (cs < e && bytes[cs] == 0) cs++;
        var ce = e; while (ce > cs && bytes[ce - 1] == 0) ce--;
        if (ce == cs) continue; // region is pure zero padding

        if (LooksLikeRecordHeader(bytes, cs) && cs + 24 <= bytes.Length)
        {
            recordLengths.Add(ReadLE32(bytes, cs + 4));
            recordPts.Add((uint)ReadLE32(bytes, cs + 16));
        }

        if (shown < 12)
        {
            Console.WriteLine($"  region 0x{s:X}..0x{e:X} ({e - s} bytes; content {ce - cs} @ 0x{cs:X}):");
            Console.WriteLine($"    {HexAscii(bytes, cs, Math.Min(ce, cs + 32))}");
            if (LooksLikeRecordHeader(bytes, cs) && cs + 24 <= bytes.Length)
                Console.WriteLine($"    -> 24-byte record header: marker={ReadLE32(bytes, cs)} len={ReadLE32(bytes, cs + 4)} id={ReadLE32(bytes, cs + 8)} flag=0x{bytes[cs + 12]:X2} pts={ReadLE32(bytes, cs + 16)}");
            for (var i = cs; i < ce - 1; i++)
            {
                if (bytes[i] == 0xFF && (bytes[i + 1] & 0xF6) == 0xF0)
                {
                    Console.WriteLine($"    -> possible ADTS-AAC syncword @ 0x{i:X}");
                    adtsSeen = true;
                    break;
                }
            }
            shown++;
        }

        audioStream?.Write(bytes, cs, ce - cs);
    }
    if (gaps.Count > shown)
        Console.WriteLine($"    … {gaps.Count - shown} more region(s) not shown");

    // 4. Summarize structure so a format guess can be made.
    if (recordLengths.Count > 0)
    {
        var distinct = recordLengths.Distinct().OrderBy(x => x).ToArray();
        Console.WriteLine($"  non-video regions starting with a record header: {recordLengths.Count}");
        Console.WriteLine($"    payload lengths: min={recordLengths.Min()} max={recordLengths.Max()} distinct={distinct.Length} [{string.Join(",", distinct.Take(8))}{(distinct.Length > 8 ? ",…" : "")}]");
        if (distinct.Length <= 2)
            Console.WriteLine("    -> near-constant payload size suggests fixed-frame audio (PCM or G.711/ADPCM), not AAC");
        var deltas = recordPts.Zip(recordPts.Skip(1), (a, b) => (long)b - a).Where(d => d > 0).ToArray();
        if (deltas.Length > 0)
            Console.WriteLine($"    pts deltas (µs): min={deltas.Min()} max={deltas.Max()} median={deltas.OrderBy(x => x).ElementAt(deltas.Length / 2)}");
    }
    if (adtsSeen)
        Console.WriteLine("  -> ADTS-AAC syncword(s) present; audio is likely AAC in ADTS framing");
}

static bool IsVideoRecordHeader(byte[] b, int i)
{
    if (i + 28 > b.Length) return false;
    if (b[i + 1] != 0 || b[i + 2] != 0 || b[i + 3] != 0) return false;
    if (b[i] != 0 && b[i] != 1) return false;
    if (b[i + 12] < 0x90) return false;
    if (b[i + 13] != 0x01 || b[i + 14] != 0 || b[i + 15] != 0) return false;
    if (b[i + 20] != 0 || b[i + 21] != 0 || b[i + 22] != 0 || b[i + 23] != 0) return false;
    // Payload must start with an HEVC Annex-B start code.
    return b[i + 24] == 0 && b[i + 25] == 0 && b[i + 26] == 0 && b[i + 27] == 1;
}

// Relaxed match: the shared record-header shape WITHOUT the video start-code requirement,
// so audio records (if they reuse the same framing) are recognized too.
static bool LooksLikeRecordHeader(byte[] b, int i)
{
    if (i + 24 > b.Length) return false;
    if (b[i + 1] != 0 || b[i + 2] != 0 || b[i + 3] != 0) return false;
    if (b[i + 13] != 0x01 || b[i + 14] != 0 || b[i + 15] != 0) return false;
    return b[i + 20] == 0 && b[i + 21] == 0 && b[i + 22] == 0 && b[i + 23] == 0;
}

static int ReadLE32(byte[] b, int i) => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);

static string HexAscii(byte[] b, int start, int endExclusive)
{
    var hex = new StringBuilder();
    var ascii = new StringBuilder();
    for (var i = start; i < endExclusive; i++)
    {
        hex.Append(b[i].ToString("X2")).Append(' ');
        ascii.Append(b[i] is >= 0x20 and < 0x7F ? (char)b[i] : '.');
    }
    return $"{hex.ToString().TrimEnd()}  |{ascii}|";
}

static string HevcNalName(int type) => type switch
{
    0 or 1 => "TRAIL slice",
    19 or 20 => "IDR slice",
    21 => "CRA slice",
    32 => "VPS",
    33 => "SPS",
    34 => "PPS",
    35 => "AUD",
    39 or 40 => "SEI",
    _ => "other"
};
