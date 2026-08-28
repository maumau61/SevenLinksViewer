# Contributing

Thanks for your interest! This is a small utility project, but contributions are welcome.

## The most valuable contribution: new firmware samples

Playback breaks whenever 7links ships a firmware that changes the `.media` record header
(see [docs/media-format.md](docs/media-format.md)). If your clips stopped playing:

1. Run the probe on one failing event folder and one working one:
   ```powershell
   dotnet run --project src/MediaProbe -- "path\to\failing-event-folder"
   ```
2. Open an issue with:
   - Your camera model and (if known) firmware version/date.
   - The first ~48 bytes of a failing `.media` file (the probe prints this).
   - The `byte[12]` value the probe reports.

That's usually enough to fix it in one line.

## Development

- **Requirements:** Windows + [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- **Build:** `dotnet build`
- **Run:** `dotnet run --project src/SevenLinksViewer`

### Guidelines

- Match the existing code style (`.editorconfig` is provided).
- Keep changes focused; explain *why* in the PR description.
- **Never commit camera footage** — `Camera/` and `*.media` are git-ignored on purpose.
- When you change `.media` parsing, bump the cache version in `MediaConcatenator.BuildPlayableHevc`
  so stale caches from older logic are discarded.

## Reporting bugs

Use the issue templates. Include the app's status-bar text and any dialog message — the parser
now surfaces a clear error for unrecognized firmware headers.
