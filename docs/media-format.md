# The 7links `.media` container format

This is a reverse-engineered description of how 7links cameras store recordings. It is **not
official** — it was worked out by inspecting real files with `src/MediaProbe`. It's accurate
enough to reliably extract and play footage across every firmware generation seen so far.

## Layout on disk

```
DCIM/
└─ <year>/<month>/<day>/
   └─ <unix-timestamp>_<id>/       ← one recorded event
      ├─ .info                     ← tiny text sidecar
      ├─ 0000.media                ← chunk 0
      ├─ 0010.media                ← chunk 1
      └─ 0020.media                ← chunk 2 …
```

- The folder name's leading integer is the **event start time** as a Unix timestamp (seconds).
- Each `.media` chunk is roughly one segment of the event (~12 s of video).

## Chunk structure

A `.media` file is a flat stream of **records**:

```
┌───────────────────────┬──────────────────────────┐
│ 24-byte record header │ HEVC Annex-B payload      │  ← repeated
└───────────────────────┴──────────────────────────┘
```

Records come in two kinds — **video** and **audio** (distinguished by the marker byte, see
below) — interleaved, with zero padding in between. When extracting video, the audio records and
padding are skipped by scanning for the next valid header; see [Audio records](#audio-records-marker-3)
for recovering sound.

### 24-byte record header

| Offset  | Size | Meaning                                                                    |
|---------|------|----------------------------------------------------------------------------|
| `00..03`| u32  | Marker: `1` = IDR access unit (VPS+SPS+PPS+IDR), `0` = P-frame TRAIL, `3` = audio |
| `04..07`| u32  | Payload length in bytes (little-endian)                                    |
| `08..11`| u32  | Per-frame id                                                               |
| `12`    | u8   | **Firmware flag byte** — see below ⚠️                                       |
| `13..15`| —    | Literal `01 00 00`                                                          |
| `16..19`| u32  | Presentation/decode timestamp, microseconds (little-endian)                |
| `20..23`| —    | Reserved, always zero                                                       |

For **video** records (marker `0`/`1`) the payload is `length` bytes of HEVC Annex-B and always
starts with a `00 00 00 01` NAL start code. **Audio** records (marker `3`) are described below.

## Audio records (marker `3`)

Audio is stored as its own records interleaved between the video ones, using the **same 24-byte
header framing** — only the marker byte `[0..3]` differs (`3` instead of `0`/`1`). There is no
start code to anchor on, so the fixed header fields (flag `[12] >= 0x90`, literal `01 00 00` at
`[13..15]`, zero `[20..23]`) carry the detection.

The payload is raw **PCM**:

| Property    | Value                              |
|-------------|------------------------------------|
| Codec       | signed 16-bit little-endian (`s16le`) |
| Sample rate | 8 kHz                              |
| Channels    | 1 (mono)                           |
| Frame size  | 640 bytes = 320 samples = 40 ms    |

Derived from the records themselves: payload length is a constant 640 bytes and the PTS delta
between consecutive audio records is 40000 µs, so 640 bytes ÷ 40 ms = 16000 B/s = 8000 Hz × 2
bytes × 1 channel. (G.711/8-bit would be 320 bytes — ruled out.) Quiet clips store near-silent
all-zero payloads; the framing is unchanged.

To recover sound, concatenate the marker-`3` payloads in PTS order into one `s16le` stream and
mux it alongside the video (the app encodes it to AAC). See
`MediaConcatenator.IsAudioRecordHeader` / `WriteCleaned` and `FFmpegService.RemuxHevcToMp4`.
Inspect the raw audio bytes of any chunk with
`dotnet run --project src/MediaProbe -- <folder> --dump-audio audio.bin`.

## ⚠️ The firmware flag byte (offset 12)

Byte `[12]` is a **per-firmware counter that only ever increases**. Its exact value is
firmware-specific, and hardcoding it silently breaks playback of clips from newer firmware.

Observed values:

| Firmware (approx.) | byte[12] |
|--------------------|----------|
| 2024               | `0x91`   |
| 2026-03 / 2026-04  | `0x9D`   |
| 2026-07            | `0x9F`   |
| 2026-08            | `0xA0`   |

> **History:** an early fix matched only the high nibble (`b[12] & 0xF0 == 0x90`). That worked
> until the 2026-08 firmware rolled the value to `0xA0`, crossing the nibble boundary and
> rejecting **every** record → zero frames → an unplayable ~12-byte file. The current parser
> uses a loose floor (`b[12] >= 0x90`) and relies on the other, much stronger header
> constraints for detection, so future bumps (`0xA1`, `0xB0`, …) keep working.

### Why "0 frames" is the failure mode

If header detection fails, `MediaConcatenator` matches no records and emits an empty HEVC
stream (just the inter-chunk end-of-sequence NALs). Downstream, ffmpeg reports
**`format hevc detected only with low score`** and the app shows **"failed to play clip."**
The parser now throws a clear *"new camera firmware"* error on a 0-frame result instead of
caching the empty stub.

## Diagnosing a new firmware generation

1. Copy one failing event folder to disk.
2. Dump it: `dotnet run --project src/MediaProbe -- "path\to\event-folder"`.
3. Compare the header of a **failing** vs a **working** file — look for the one changed byte
   (historically `[12]`).
4. Widen the check in `MediaConcatenator.IsRecordHeader`, and **bump the cache version**
   (`.vN.h265` in `BuildPlayableHevc`) so stale empty caches are discarded.

See `src/SevenLinksViewer/Services/MediaConcatenator.cs` for the authoritative implementation.
