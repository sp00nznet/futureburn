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

**v0.0.3 — drive capabilities + disc inspection.** We now ask each drive what kinds of media it can read and write, decode every MMC profile / feature code we know about, and gracefully expose the ones we don't know yet as raw hex. Same hand-rolled COM, same `dynamic`, no wrapper packages.

```
E:\futureburn
├── Futureburn.sln
├── Directory.Build.props
├── CHANGELOG.md
└── src/
    ├── Futureburn.Core/
    │   └── Imapi/
    │       ├── Mmc.cs               <- profile + feature code lookup tables
    │       ├── OpticalDrive.cs      <- drive record (vendor, profiles, feature pages, ...)
    │       ├── LoadedDisc.cs        <- what's in the drive right now
    │       ├── DriveEnumerator.cs   <- enumerate + Find(identifier)
    │       └── DiscInspector.cs     <- inspect media via MsftDiscFormat2Data
    ├── Futureburn.Cli/         <- has `drives`, `drives -v`, `disc <drive>`
    └── Futureburn.Gui/         <- still an empty WPF window
```

**Up next:** v0.1 — burn an audio CD from the command line. That means decoding MP3/FLAC into 16-bit 44.1kHz stereo PCM, picking the right IMAPI2 format object for audio (`MsftDiscFormat2RawCD` and `MsftDiscFormat2TrackAtOnce`), and figuring out the CSV format we want to feed in.

---

## Running it

```powershell
# What does the program think it is?
dotnet run --project src/Futureburn.Cli

# What optical drives does Windows see, and what can each one do?
dotnet run --project src/Futureburn.Cli -- drives

# Show every profile and feature page the drive reports (raw codes included)
dotnet run --project src/Futureburn.Cli -- drives -v

# What's in drive F: right now?
dotnet run --project src/Futureburn.Cli -- disc F:
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

GUI runs but is still a blank window:

```powershell
dotnet run --project src/Futureburn.Gui
```

---

## Versioning

One number, one place. Edit `<Version>` in `Directory.Build.props` and every project picks it up. The CLI reads it back at runtime via `AssemblyInformationalVersionAttribute`, so you can always ask the binary what it thinks it is.

Each version's notes live in [CHANGELOG.md](./CHANGELOG.md).

---

## License

This is a private learning project. No license, no warranty, no promises. Burns at your own risk.
