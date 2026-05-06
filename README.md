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

**v0.0.2 — IMAPI2 says hi.** We can see optical drives now. Hand-rolled COM, no NuGet wrapper, just `Type.GetTypeFromProgID` and `dynamic`. Feels like the year 2007 in here, in a good way.

```
E:\futureburn
├── Futureburn.sln
├── Directory.Build.props      <- centralized version + shared compile settings
├── CHANGELOG.md
└── src/
    ├── Futureburn.Core/
    │   └── Imapi/
    │       └── DriveEnumerator.cs   <- talks to IMAPI2, returns OpticalDrive records
    ├── Futureburn.Cli/         <- console app, has a `drives` command now
    └── Futureburn.Gui/         <- WPF app, still an empty window
```

**Up next:** capabilities — for each drive, ask IMAPI2 what media it can write (CD-R? DVD+R DL? BD-R?). Once we know that, we pick a drive that can write CD-R and start writing the actual audio CD burn path. That's the road to v0.1.

---

## Running it

```powershell
# What does the program think it is?
dotnet run --project src/Futureburn.Cli

# What optical drives does Windows see?
dotnet run --project src/Futureburn.Cli -- drives
```

Sample output:

```
futureburn v0.0.2

Found 2 optical drives:

  F:\
    Vendor:   HL-DT-ST
    Product:  DVDRAM GE20LU10
    Revision: FE06
    Id:       \\?\usbstor#cdrom&ven_hl-dt-st&prod_dvdram_ge20lu10&rev_fe06#...

  G:\
    Vendor:   Msft
    Product:  Virtual DVD-ROM
    ...
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
