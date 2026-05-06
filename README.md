# futureburn

The optical-disc burning software your 2003 self wishes you had right now.

A passion project to build a friendly, modern CD/DVD burner for **Windows 11** — both a CLI you can pipe things into and a GUI for the "I just want to click *burn* and walk away" crowd.

> Yes, optical media is dying. No, we don't care. Some of us still have spindles.

---

## What we're building

A two-front assault on the burning problem:

**The CLI** — for keyboard people.
```powershell
futureburn thisismycd.csv
```
Hand it a CSV of audio tracks, it burns the CD. That's the dream. Eventually it'll do data discs and DVDs too.

**The GUI** — for everybody else.
A normal Windows app with a menu bar and four big friendly tiles:

| | |
|---|---|
| **Burn Audio CD** | **Burn Video DVD** |
| **CD Info** | **Settings** |

Click a tile, get a focused little sub-program. Audio = drop your MP3s in, drag to reorder, hit burn. Video = drop your MKV in, hit burn (no menus, no chapters, just the movie). CD Info = tell me what's on this disc. Settings = the boring but necessary stuff.

---

## Roadmap

We're going in order. No skipping.

- [ ] **v0.1 — Burn one audio CD from the command line.** The "hello world" of optical media.
- [ ] **v0.2 — The four-tile GUI shell.** No actual burning behind the tiles yet, just the frame.
- [ ] **v0.3 — Audio CD GUI.** Drag, drop, reorder, burn.
- [ ] **v0.4 — Data CD/DVD.** Files in, disc out.
- [ ] **v0.5 — Video DVD.** MKV in, watchable-on-a-DVD-player disc out. This one's going to be a fight.
- [ ] **v0.6 — CD Info tile.** Read disc, show contents, show track times, the works.
- [ ] **v?.? — LightScribe.** The white whale. HP killed LightScribe in 2013. The SDK is out there, somewhere. We will find it. We will burn cat pictures onto disc labels.
- [ ] **v∞ — Mac/Linux ports.** Long after Windows is solid. Maybe never. We'll see.

---

## Stack

- **Language:** C# (.NET 8)
- **GUI:** WPF (chosen over WinUI 3 for the better learning resources — there's a decade of Stack Overflow answers)
- **Burning engine:** [IMAPI2](https://learn.microsoft.com/en-us/windows/win32/imapi/about-imapiv2) — Windows' built-in COM API for optical media. C# talks to COM without much fuss.
- **Target OS:** Windows 11. Win10 will probably work but isn't a goal.

---

## Status

**v0.0.6 — the burn pipeline (untested on hardware as of writing).** The CLI now has a `burn` command that takes a playlist and a drive. The code path:

1. Validates the playlist (every track exists + is decodable)
2. Decodes any non-CD-format tracks to a temp dir (skips files that are already 44.1k / 16-bit / stereo WAV — the common case for Spotify rips)
3. Pre-checks the disc — friendly bail-out if the CD-R is already used (you'll see the helpful message instead of a SCSI mode page error)
4. Queries the disc via `MsftDiscFormat2TrackAtOnce` — sectors free, existing tracks, supported write speeds
5. Validates: speed is in the supported list, capacity holds the playlist, disc is blank (or `--force`)
6. In `--dry-run`: prints the plan and exits
7. In real burn: y/N confirmation (skip with `--yes`), then `PrepareMedia` → `AddAudioTrack` per track → `ReleaseMedia`

Hand-rolled COM throughout — the chosen TAO properties (`NumberOfExistingTracks`, `TotalSectorsOnMedia`, `SupportedWriteSpeeds`) all live on `IDiscFormat2TrackAtOnce` directly, no inherited base members needed, so we still don't need typed `[ComImport]` interfaces. Streaming uses a custom `ManagedIStream` (.NET Stream → COM IStream adapter) and a `CdPaddedAudioStream` (strips WAV header, pads each track to a 2352-byte CD sector boundary as IMAPI demands).

**Status of the actual burning:** the code builds clean and the dry-run path runs end-to-end through Plan(). Real-disc validation is pending — needs a fresh blank CD-R in the drive.

The four-tile GUI from v0.0.4 still hasn't been wired to the burn flow — that's v0.0.8.

```
E:\futureburn
├── Futureburn.sln
├── Directory.Build.props
├── CHANGELOG.md
└── src/
    ├── Futureburn.Core/
    │   ├── Imapi/                  <- IMAPI2: drive enum, capabilities, disc inspection
    │   └── Audio/
    │       ├── CdFormat.cs         <- Red Book audio constants
    │       ├── AudioDecoder.cs     <- decode + resample anything → CD-format WAV
    │       └── Playlist.cs         <- M3U / M3U8 parser
    ├── Futureburn.Cli/             <- `drives`, `disc`, `probe`, `decode`, `playlist`
    └── Futureburn.Gui/             <- MainWindow + CdInfoWindow + PlaceholderWindow
```

**Up next:** v0.0.6 — actually burn the bytes. Wire up `MsftDiscFormat2RawCD` (or `MsftDiscFormat2TrackAtOnce`) and feed it the CD-format PCM stream. This is also when we declare a typed `[ComImport] IDiscFormat2` so we get authoritative blank/finalized status before pressing the big red button. After that, v0.0.7 = multi-track from an M3U, then v0.0.8 wires the same flow into the **Burn Audio CD** GUI tile, and v0.1.0 is polish + safety checks.

---

## Running it

```powershell
# Drives + capabilities
dotnet run --project src/Futureburn.Cli -- drives
dotnet run --project src/Futureburn.Cli -- drives -v
dotnet run --project src/Futureburn.Cli -- disc F:

# Audio
dotnet run --project src/Futureburn.Cli -- probe song.mp3
dotnet run --project src/Futureburn.Cli -- decode song.mp3 song-cd.wav
dotnet run --project src/Futureburn.Cli -- playlist mix.m3u8
```

Sample `drives` output:

```
Found 2 optical drives:

  F:\  HL-DT-ST DVDRAM GE20LU10 (FE06)
    Reads:  CD-ROM; DVD-ROM
    Writes: CD-R, CD-RW; DVD-RAM, DVD-R Sequential, DVD-R DL Sequential, ...
    Loaded: CD-R

  G:\  Msft Virtual DVD-ROM (1.0)
    Reads:  CD-ROM; DVD-ROM
    Loaded: DVD-ROM
```

GUI:

```powershell
dotnet run --project src/Futureburn.Gui
```

Opens the four-tile main window. Click **CD Info** for a live drive/disc browser. The other three tiles open "not done yet" placeholders that point at the roadmap.

---

## Versioning

One number, one place. Edit `<Version>` in `Directory.Build.props` and every project picks it up. The CLI reads it back at runtime via `AssemblyInformationalVersionAttribute`, so you can always ask the binary what it thinks it is.

Each version's notes live in [CHANGELOG.md](./CHANGELOG.md).

---

## License

This is a private learning project. No license, no warranty, no promises. Burns at your own risk.
