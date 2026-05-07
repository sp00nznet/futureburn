# Changelog

All notable changes to futureburn will land here. Format roughly follows [Keep a Changelog](https://keepachangelog.com/), versioning is [SemVer](https://semver.org/) — though while we're pre-1.0 the rules are loose: minor version = a milestone from the README roadmap, patch version = everything else.

## [Unreleased]

## [0.0.16] — 2026-05-06

### Added
- **Folder → ISO image builder.** New `Futureburn.Core/Fs/FsImageBuilder.cs` wraps Windows' `IMAPI2FS.MsftFileSystemImage` COM via dynamic dispatch (same pattern as our IMAPI v2 work — no NuGet wrappers). Builds ISO 9660 + Joliet + UDF in any combination, configurable volume label, sequential-byte output to either a `Stream` or a file path.
- CLI: `mkiso <folder> <output.iso>` builds the image to disk. Flags: `--label NAME`, `--fs all|iso|joliet|udf`.
- CLI: `burn-folder <folder> <drive>` does mkiso + burn-iso in one step (writes to a temp ISO, burns it, cleans up). Flags: `--label`, `--fs`, `--speed`, `--dry-run`, `--yes`, `--keep-iso`.
- GUI: BurnImageWindow gains a **Choose folder...** button. Builds the ISO in the background (Task.Run + Dispatcher progress updates), displays the source folder + temp ISO path together, cleans up the temp file when the window closes.

### Validated
- Built a 348.63 MB / 178,496-sector ISO from the 5-track whale-album folder. Mounted it via Windows (`Mount-DiskImage`); all 6 files (5 WAVs + playlist.m3u8) readable with correct sizes. ISO 9660 magic "CD001" present at byte 0x8001.

### Notes
- Initial implementation had wrong `FsiFileSystems` enum values (guessed 0x02/0x04/0x08; spec says 0x01/0x02/0x04). IMAPI2FS rejected the bogus combo with "value specified for parameter 'newVal' is not valid." Fixed and noted with a `// must match IMAPI2FS's FsiFileSystems` comment so we don't drift again.

## [0.0.15] — 2026-05-06

### Added
- **ISO image burning** to blank CD-R or DVD-R via raw SCSI (no IMAPI involved). New `Futureburn.Core/Spti/SptiDataBurner.cs` plus CLI command `burn-iso <iso> <drive>` with `--dry-run`, `--speed Nx`, `--yes` flags. Detects disc type from the loaded profile and picks CD-data or DVD-data MODE SELECT settings appropriately. WRITE 12 in 32-sector (64 KB) chunks with the same retry-on-Win32-121 logic the audio burner uses.
- `SptiDevice.ConfigureForDataCd()` / `ConfigureForDataDvd()` — Mode Page 0x05 setup for data writes (Mode 1, 2048 bytes per sector, BUFE on, TAO for CD / SAO for DVD).
- **Burn Blu-ray / DVD GUI tile is real.** `BurnImageWindow.xaml` lets you choose an ISO, pick a drive + speed, and burn. Same background-thread + Dispatcher pattern as `BurnAudioCdWindow`. Shows which standard disc capacities the chosen image fits on (CD-R / DVD-R / DVD-R DL / BD-R).
- README's "What's coming" list reorganized to clarify the realistic next steps: folder → ISO builder, then MKV → DVD-Video transcoding (the latter being a separate large subsystem). Blu-ray burning waits for hardware.

### Notes
- ISO burning **assumes the image is pre-authored**. We don't build the file system from a folder yet — that's a separate (smaller) future task using either IMAPI's `MsftFileSystemImage` or our own UDF/ISO 9660 writer. Building DVD-Video discs from raw video files is a much bigger task involving MPEG-2 encoding and IFO/BUP/VOB authoring; that's the long-arc goal for the Burn Blu-ray / DVD tile.

## [0.0.10] — 2026-05-06

### Added
- **CD Info GUI tile now reads real disc info via SPTI.** When you select a drive with a disc loaded, the details pane shows: disc finalization status, last session state, session count, disc type, layout (audio CD / data / mixed), and a full track listing with per-track type and duration. Same data the CLI's `cd-info` command surfaces, just clickable.
- Failures degrade gracefully — if SCSI pass-through is unavailable for any reason, you still see the IMAPI-based info above it.

## [0.0.9] — 2026-05-06

### Added
- `SptiDevice.ReadDiscInformation()` — SCSI MMC READ DISC INFORMATION (opcode 0x51). Returns Disc Status (Empty/Incomplete/Finalized/Other), State of Last Session (Empty/Incomplete/Reserved/Complete), session count, disc type, and erasable flag. This is the authoritative answer to "is this disc finalized?" — finalized + complete = will play in any standalone CD player.
- `cd-info` now reports the disc-info fields above before the TOC, with a friendly "will play in standalone players" / "NOT fully finalized" annotation.

### Fixed
- Initial bit parsing of READ DISC INFORMATION byte 2 was wrong — had Disc Status in bits 7-6, but per MMC-6 it's in bits 1-0 (and State of Last Session is in bits 3-2). Caught when a known-finalized 19-track audio CD reported "Empty" with "Reserved" session state. Fixed to match the spec.

### Validated
- The user's previously-burned puck disc (CDBurnerXP from the same playlist) reports **Disc Status: Finalized, Last Session: Complete** — definitive proof the disc is structurally fine. Any playback failure (VLC's longstanding Windows CD-DA bugs being the prime suspect) is a player-side issue.

## [0.0.8] — 2026-05-06

### Added
- `SptiDevice.ReadToc()` + `cd-info <drive>` CLI command. SCSI READ TOC/PMA/ATIP via SPTI. Returns first/last track numbers, lead-out LBA, plus per-track type (audio vs data, with pre-emphasis flag), start LBA, length, and duration. Works on any CD with a readable TOC — audio CDs, mixed-mode, finalized CD-Rs.

### Validated
- Read the puck disc (a finalized 19-track audio CD that CDBurnerXP burned from the user's same playlist earlier). Result: 19 audio tracks, lead-out at LBA 281,457, total 01:02:32. **Matches our Plan() output for the same playlist track-for-track.** End-to-end proof that our pipeline computes the right disc layout.

## [0.0.7] — 2026-05-06

### Added
- **IMAPI v1 burn engine.** `Futureburn.Core/Imapi/AudioCdBurnerV1.cs` and `ImapiV1Interop.cs`. Typed `[ComImport]` declarations for `IDiscMaster`, `IDiscRecorder`, `IEnumDiscRecorders`, `IRedbookDiscMaster`. Used as a fallback for drives where IMAPI v2's TAO path returns a SCSI mode-page error on blank CD-Rs (LG GE20LU10 firmware FE06 is the known case). Selected via `futureburn burn ... --engine v1`.
- CLI: `imapi-v1-info` — non-destructive diagnostic that opens v1, enumerates recorders, and reports Redbook capabilities. Validated on the LG drive: v1 sees the drive correctly while v2 chokes.
- **SPTI scaffold.** `Futureburn.Core/Spti/{SptiNative,MmcOpcodes,SptiDevice,SptiBurnEngine}.cs`. P/Invoke for `IOCTL_SCSI_PASS_THROUGH_DIRECT`, MMC opcode constants, drive-opener that talks raw SCSI. `SptiBurnEngine` is a stub for now — full audio CD burn via raw SCSI is the work that comes if v1 also fails on someone's hardware.
- CLI: `spti-info <drive>` — runs a SCSI INQUIRY via SPTI. Validated end-to-end on the LG drive: returns vendor/product/firmware identical to IMAPI's view, proving the SPTI pipeline works.

### Fixed
- `BurnPlan` time displays were using `mm\\:ss` format which capped at 59:59 — burned playlists over an hour showed mangled minutes ("14:00" for a 74-min disc). Now uses `hh\\:mm\\:ss`.
- Wrong field referenced when computing v1 plan total time (was reading disc capacity instead of track sum).

### Investigation notes (in case future-us forgets)
- IMAPI v1 interfaces are vtable-only IUnknown. PowerShell can't talk to them at all. Typed `[ComImport]` is the only way in.
- The LG GE20LU10 FE06 returns "mode page not present" from `IDiscFormat2TrackAtOnce::PrepareMedia` for blank CD-R, even with `AcquireExclusiveAccess(force=true)` and `DisableMcn`. Bare PowerShell IMAPI hits the same error — it's not our code.
- ImgBurn talks to the same drive successfully because it uses SPTI directly (not IMAPI). That's why we have the SPTI scaffold ready.
- IMAPI 2 IIDs do NOT all share the `7F64` second segment that IDiscMaster2 uses. Look up actual GUIDs from `HKLM:\SOFTWARE\Classes\Interface\` rather than guessing.

## [0.0.6] — 2026-05-06

### Added
- `Futureburn.Core/Imapi/AudioCdBurner.cs` — two-phase burn pipeline. `Plan()` validates the request and returns a `BurnPlan`; `ExecuteBurn()` actually writes. Uses `MsftDiscFormat2TrackAtOnce` via dynamic COM. No typed `[ComImport]` interfaces required — every TAO property we touch (`NumberOfExistingTracks`, `TotalSectorsOnMedia`, `FreeSectorsOnMedia`, `SupportedWriteSpeeds`) lives on `IDiscFormat2TrackAtOnce` directly.
- `Futureburn.Core/Imapi/ManagedIStream.cs` — adapts a .NET `Stream` to a COM `IStream` so we can pass it to `AddAudioTrack`. Marked `[ComVisible(true)]` because the assembly default is false.
- `Futureburn.Core/Audio/CdPaddedAudioStream.cs` — wraps a CD-format WAV file as raw PCM bytes padded to 2352-byte CD sectors (IMAPI's hard requirement).
- CLI: `burn <playlist> <drive>` with `--dry-run`, `--speed Nx`, `--force`, `--yes` / `-y`, `--keep-temp` flags.
- Smart pre-check: if `MsftDiscFormat2Data` can't read the loaded CD-R/CD-RW's capacity, we abort with a friendly "this disc isn't fresh" message instead of letting `PrepareMedia` fail later with a cryptic SCSI mode page error.
- Tracks already in CD format (44.1k / 16-bit / stereo WAV) are passed through directly — no pointless re-decode of huge WAV files.
- IMAPI track minimum-length enforcement (4 seconds = 300 sectors) to refuse tracks too short for CD-DA.

### Notes (a.k.a. things we learned)
- `IDiscFormat2TrackAtOnce.PrepareMedia()` must be called before `NumberOfExistingTracks`, sector counts, and `SupportedWriteSpeeds` are readable. PrepareMedia reserves the drive but writes nothing — releasing the COM object without `ReleaseMedia` aborts the session cleanly (no `AddAudioTrack` was issued, so nothing was committed to the disc).
- For our test CD-R (already had data), both `MsftDiscFormat2Data` and `MsftDiscFormat2TrackAtOnce` refused to read it — exactly the expected symptom of a write-once disc that's already been used. The pre-check now catches this politely.

### Verified
- Builds clean. CLI prints the right `usage` text for `burn`.
- Dry-run with the test playlist successfully reaches Plan() and surfaces the "disc isn't fresh" pre-check error when given a non-blank CD-R.

### Pending
- Real-hardware test with a fresh blank CD-R. Will update the changelog and README once we've completed an actual burn end-to-end.

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
