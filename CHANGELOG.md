# Changelog

All notable changes to futureburn will land here. Format roughly follows [Keep a Changelog](https://keepachangelog.com/), versioning is [SemVer](https://semver.org/) — though while we're pre-1.0 the rules are loose: minor version = a milestone from the README roadmap, patch version = everything else.

## [Unreleased]

## [0.0.5] — 2026-05-06

### Added
- **NAudio** (2.3.0) added to `Futureburn.Core`. First — and so far only — third-party audio dependency. Justified because writing MP3/AAC/FLAC decoders from scratch is a separate career.
- `Futureburn.Core/Audio/CdFormat.cs` — Red Book audio constants (44.1 kHz, 16-bit, stereo, 2352-byte sectors, 75 sectors/sec) plus duration/sector math helpers.
- `Futureburn.Core/Audio/AudioDecoder.cs` — `Probe()` returns format/duration without decoding; `DecodeToCdWav()` decodes any supported file and writes a CD-format WAV. Inputs that are already CD-format skip the resampler entirely. WAV uses NAudio's lightweight `WaveFileReader`; MP3 / M4A / AAC / WMA / FLAC go through Windows Media Foundation via `MediaFoundationReader`.
- `Futureburn.Core/Audio/Playlist.cs` — M3U / M3U8 parser. Handles both simple (paths only) and extended (`#EXTM3U` + `#EXTINF:<seconds>,<title>`) flavors. Relative paths resolved against the playlist's directory.
- CLI: `probe <file>`, `decode <in> <out.wav>`, `playlist <file.m3u>`. The probe command tells you whether resampling will be needed.

### Verified
- Decoded `C:\Windows\Media\Alarm01.wav` (22050 Hz / stereo / 16-bit) → CD-format WAV (44100 Hz / stereo / 16-bit). Round-tripped probe confirms `IsCdFormat: yes — no resampling needed`.
- Extended M3U with a missing-file entry parses correctly, marks the missing track with `?`, and reports the total duration.

### Burns
- Still nothing! But we can now produce the exact bytes a CD wants. v0.0.6 carries those bytes to a disc.

## [0.0.4] — 2026-05-06

### Added
- **The four-tile shell.** `MainWindow` is now a 2×2 grid of big tile buttons (Burn Audio CD / Burn Video DVD / CD Info / Settings) with a menu bar (File → Exit, Help → About) and a status bar. Each tile has a quippy subtitle.
- `CdInfoWindow` — the **CD Info** tile opens a real sub-program: a drive list on the left, a live details pane on the right (capabilities, loaded media, capacity, write speeds), plus a Refresh button. Same data the CLI's `drives` and `disc` commands show, just interactive.
- `PlaceholderWindow` — parameterized "this ships in v0.X" dialog reused by the other three tiles.
- About dialog with the GitHub URL, because every passion project deserves an About box.

### Notes
- Code-behind, not MVVM. We'll graduate to MVVM if/when bindings get hairy enough to earn the ceremony.
- No third-party packages. WPF defaults all the way down.

### Burns
- Still nothing. But you can now click on **Burn Audio CD** and read a polite refusal in window form.

## [0.0.3] — 2026-05-06

### Added
- `Futureburn.Core/Imapi/Mmc.cs` — lookup tables for MMC profile codes (CD-R, DVD+R DL, BD-RE, HD DVD-RAM, ...) and feature pages, plus the IMAPI media physical type enum. Unknown codes are still surfaced as raw hex with an "Unknown" label so weird drives (Xbox 360, exotic Blu-ray formats) stay visible.
- `Futureburn.Core/Imapi/OpticalDrive.cs` — extracted into its own file, now carries `SupportedProfiles`, `CurrentProfiles`, `SupportedFeaturePages`, `CurrentFeaturePages`, `CanLoadMedia`, plus `WritableProfiles` / `ReadOnlyProfiles` / `PrimaryMount` conveniences.
- `Futureburn.Core/Imapi/LoadedDisc.cs` — record describing what's in the drive: media type, sectors total/free, next writable address, current + supported write speeds, blank flag. Has a `HasFormatDetails` flag for when MsftDiscFormat2Data can't read the disc (audio CDs, finalized media, ROM).
- `Futureburn.Core/Imapi/DiscInspector.cs` — uses MsftDiscFormat2Data to read media info. Fails gracefully on non-data discs.
- `DriveEnumerator.Find(identifier)` — look up a drive by mount point ("F", "F:", "F:\\") or by unique id.
- CLI: `drives -v` / `drives --verbose` dumps every supported profile and feature page (raw codes shown for unknown ones).
- CLI: `disc <drive>` inspects the loaded media in a drive.

### Notes (a.k.a. things we learned the hard way)
- The IDispatch on `MsftDiscFormat2Data` doesn't expose the inherited `IDiscFormat2` base members (CurrentMediaType, MediaPhysicallyBlank, etc.) when we go through C# `dynamic`. To stay hand-rolled (no `[ComImport]` interface declarations), we derive media type from the drive's CurrentProfile and infer "blank" from `FreeSectors == TotalSectors`. If we ever need authoritative state, the next step is to declare a typed IDiscFormat2 and cast.

### Burns
- Still nothing. But we now know exactly which of your drives can write CD-R, DVD-R DL, or whatever exotic format you've got plugged in.

## [0.0.2] — 2026-05-06

### Added
- `Futureburn.Core/Imapi/DriveEnumerator.cs` — hand-rolled IMAPI2 access via `Type.GetTypeFromProgID` + `dynamic`. No COM interface declarations, no NuGet wrappers. Returns a list of `OpticalDrive` records (vendor, product, firmware revision, mount points, unique id).
- `futureburn drives` CLI command. Lists every optical drive Windows can see.
- `futureburn help` / `--help` / `-h` for usage.

### Changed
- All three projects now target `net8.0-windows` (was `net8.0` for Core/Cli, already `net8.0-windows` for Gui). We're Win11-only, so this kills CA1416 warnings on Windows-specific APIs without scattering `[SupportedOSPlatform]` everywhere.
- Hoisted `TargetFramework`, `Nullable`, `ImplicitUsings` into `Directory.Build.props`. The csprojs are now mercifully short.
- Deleted the `Class1.cs` placeholder that came with `dotnet new classlib`.

### Burns
- Still nothing. But we know who's *capable* of burning now, which is progress.

## [0.0.1] — 2026-05-06

### Added
- Empty .NET 8 solution with three projects: `Futureburn.Core` (class library), `Futureburn.Cli` (console app), `Futureburn.Gui` (WPF app).
- Both `Cli` and `Gui` reference `Core`.
- Centralized version + metadata in `Directory.Build.props` so bumping a version means editing one line.
- Repo, README, .gitignore, and this changelog.

### Burns
- Nothing yet. We are pre-burn. Like pre-Cambrian, but with less life.
