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

Between records the file also contains audio fragments and zero padding, which are **not**
video and must be skipped by scanning for the next valid header.

### 24-byte record header

| Offset  | Size | Meaning                                                                    |
|---------|------|----------------------------------------------------------------------------|
| `00..03`| u32  | Marker: `1` = IDR access unit (VPS+SPS+PPS+IDR), `0` = P-frame TRAIL        |
| `04..07`| u32  | Payload length in bytes (little-endian)                                    |
| `08..11`| u32  | Per-frame id                                                               |
| `12`    | u8   | **Firmware flag byte** — see below ⚠️                                       |
| `13..15`| —    | Literal `01 00 00`                                                          |
| `16..19`| u32  | Presentation/decode timestamp, microseconds (little-endian)                |
| `20..23`| —    | Reserved, always zero                                                       |

The payload immediately following the header is `length` bytes of HEVC Annex-B and always
starts with a `00 00 00 01` NAL start code.

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
