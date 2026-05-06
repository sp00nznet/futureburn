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

**v0.0.4 — the four-tile shell is up.** The GUI now opens to the main window from your sketch: four big tiles in a 2×2 grid, plus a menu bar and status bar.

```
+---------------------------+---------------------------+
|     Burn Audio CD         |     Burn Video DVD        |
|  MP3s in. Disc out.       |  MKV in. Watchable disc.  |
+---------------------------+---------------------------+
|         CD Info           |        Settings           |
|  What is this disc?       |  The boring stuff.        |
+---------------------------+---------------------------+
```

**CD Info** is the only tile with real meat behind it for now — it lists optical drives, lets you click one, and shows a live readout of capabilities + loaded media (the same view as the CLI's `disc` command, but interactive). The other three tiles open friendly placeholder windows that say "ships in v0.X."

```
E:\futureburn
├── Futureburn.sln
├── Directory.Build.props
├── CHANGELOG.md
└── src/
    ├── Futureburn.Core/
    │   └── Imapi/
    │       ├── Mmc.cs               <- profile + feature code lookup tables
    │       ├── OpticalDrive.cs
    │       ├── LoadedDisc.cs
    │       ├── DriveEnumerator.cs
    │       └── DiscInspector.cs
    ├── Futureburn.Cli/         <- `drives`, `drives -v`, `disc <drive>`
    └── Futureburn.Gui/
        ├── MainWindow.xaml          <- the four-tile shell + menu bar
        ├── CdInfoWindow.xaml        <- drive list + live disc info
        └── PlaceholderWindow.xaml   <- "ships in v0.X" for the other tiles
```

**Up next:** v0.1 — burn an audio CD. The big leap. Probably split into milestones like:
- v0.0.5: pull in NAudio, decode an MP3 to PCM, write a `.wav` to disk
- v0.0.6: wire up `MsftDiscFormat2RawCD` and burn a single track to a blank CD-R
- v0.0.7: multi-track from a CSV (`futureburn tracks.csv`)
- v0.0.8: same flow in the GUI's **Burn Audio CD** tile (drag-drop + reorder)
- v0.1.0: polish, error messages, test on a few different blank discs

Somewhere in there we'll also do the typed `[ComImport] IDiscFormat2` work to get authoritative blank/finalized state — the **CD Info** tile would benefit from it, and we'll need it for safety checks before burning.

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
