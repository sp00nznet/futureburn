# futureburn

A free, modern, open-source CD/DVD burner for **Windows 11**. Both a command-line tool and a GUI. **MIT licensed. Free forever. No spyware, no installers, no bundled VPNs, no "free trial" nag screens, no telemetry, no accounts.**

> The current state of CD/DVD burning software in 2026 is grim. The "free" tools mostly come bundled with junkware — sneaky toolbar installers, "registry cleaners," VPN trial offers, telemetry. The "premium" ones charge subscription fees for software that hasn't meaningfully changed since 2008. This shouldn't be a market. **Optical disc burning is a solved engineering problem.** The Windows APIs and SCSI MMC commands needed are documented and stable. So here's the deal: futureburn is MIT-licensed, the code lives on GitHub, anyone can build it, fork it, ship it, audit it. **If anyone tries to sell you this software, they're scamming you.** Walk away.

A passion project that grew teeth.

---

## What works today

- ✅ **Burning a real Red Book audio CD from raw SCSI MMC commands** — confirmed on a 2008-vintage USB writer that two of the three IMAPI paths refused to use. Verified the disc plays in third-party players.
- ✅ Multi-format input: **WAV, MP3, M4A, AAC, WMA, FLAC** (anything Windows Media Foundation handles)
- ✅ **M3U / M3U8** playlists, simple and extended (`#EXTM3U` + `#EXTINF:`)
- ✅ Three burn engines: modern IMAPI v2, legacy IMAPI v1, and **raw SPTI/SCSI** (the path ImgBurn uses)
- ✅ Full disc inspection: drive capabilities, supported profiles + feature pages, current media type, finalization status, complete TOC with per-track type and duration
- ✅ A four-tile WPF GUI shell, **CD Info** tile is fully wired
- ✅ `--dry-run`, `--speed Nx`, `--force`, `--yes`, `--keep-temp` flags
- ✅ Salvage operation for partial burns (`finalize <drive>`)
- ✅ **ISO image burning to CD-R or DVD-R/+R/-RW/+RW** via raw SCSI (`burn-iso` CLI + Burn Blu-ray / DVD GUI tile)
- ✅ True gapless DAO via SPTI cue sheet (experimental — `--gapless` flag; first hardware test pending)

## What's coming

- ⬜ Multi-track full-album burns (working through a track-2 OS timeout edge case on the test drive)
- ⬜ Folder → ISO 9660 / Joliet / UDF builder (so you can drag a folder of files in instead of pre-building an ISO)
- ⬜ MKV → DVD-Video pipeline (transcode + IFO/BUP/VOB authoring + UDF burn — a separate large subsystem)
- ⬜ Blu-ray burning (when the test hardware arrives)
- ⬜ LightScribe support — yes, really. The white whale. HP killed it in 2013, the SDK is out there, we'll find it.
- ⬜ Mac/Linux ports — long after Windows is rock solid

---

## Screenshots

The four-tile main window — pick what you want to do:

![futureburn main window](docs/screenshots/main-window.png)

**Burn Audio CD** — drag in tracks, pick a drive, hit Burn. Background-thread burn with live progress and post-burn verification:

![Burn Audio CD window](docs/screenshots/burn-audio-cd.png)

**CD Info** — full SCSI readout of whatever's loaded: drive capabilities, finalization status, and complete TOC with per-track type and duration. Same data the CLI's `cd-info` command surfaces, just clickable:

![CD Info window](docs/screenshots/cd-info.png)

---

## Why this exists

A non-trivial chunk of the working internet still has CD/DVD drives plugged in and is producing physical media — for cars without aux jacks, for archival, for art objects, for the principle. The software market that serves these people is hostile by default. CD-burning shareware in 2026 is what shareware was in 2002, except the bundled spyware is more sophisticated.

**futureburn is an alternative**: open-source, no-strings, no-account-required, no-network-calls. The repo, the code, the binaries — all free. If you want to inspect every SCSI command we send to your drive, the code is right there. If you want to fork it and add a feature, please do. If you want to make money helping people burn discs, sell support contracts or USB writers, not the software.

---

## Engines

CD writing on Windows has three layers, and not all of them work on every drive. futureburn supports all three so we can pick whatever the hardware likes:

| Flag | Layer | Use when |
|---|---|---|
| `--engine v2` *(default)* | IMAPI 2 (the modern Windows COM API, `MsftDiscFormat2TrackAtOnce`) | Most modern drives. Just works. |
| `--engine v1` | IMAPI 1 (legacy XP-era COM, `MsDiscMasterObj` + `IRedbookDiscMaster`) | When v2 returns "mode page not present" or fails for unclear reasons on an old drive. |
| `--engine spti` | Raw SCSI Pass-Through (`IOCTL_SCSI_PASS_THROUGH_DIRECT` + MMC opcodes) | When both IMAPI versions are uncooperative. Same approach as ImgBurn. **This is the engine that successfully wrote our first real audio CD.** |

Run the diagnostics to find out which one your drive likes:

```powershell
futureburn drives -v          # full capability dump for every drive
futureburn cd-info F          # finalization status + TOC for the disc in F:
futureburn imapi-v1-info      # is IMAPI v1 functional on this PC?
futureburn spti-info F        # does raw SCSI pass-through work on F:?
```

---

## Stack

- **Language:** C# (.NET 8)
- **GUI:** WPF
- **Audio:** [NAudio](https://github.com/naudio/NAudio) (MIT) for decoding + resampling
- **Burning:**
  - IMAPI v2 + v1 via hand-rolled `[ComImport]` interfaces (no NuGet wrappers)
  - SPTI via direct P/Invoke and SCSI MMC opcodes
- **Target OS:** Windows 11 (Win10 likely works, not a goal)
- **Third-party packages:** NAudio. That's it. No installer, no telemetry SDK, no analytics package.

---

## Running it

```powershell
# Drives + capabilities
dotnet run --project src/Futureburn.Cli -- drives
dotnet run --project src/Futureburn.Cli -- drives -v

# Disc inspection
dotnet run --project src/Futureburn.Cli -- disc F:
dotnet run --project src/Futureburn.Cli -- cd-info F

# Audio probing / decoding
dotnet run --project src/Futureburn.Cli -- probe song.mp3
dotnet run --project src/Futureburn.Cli -- decode song.mp3 song-cd.wav
dotnet run --project src/Futureburn.Cli -- playlist mix.m3u8

# Burning
dotnet run --project src/Futureburn.Cli -- burn mix.m3u8 F: --dry-run
dotnet run --project src/Futureburn.Cli -- burn mix.m3u8 F: --engine spti --speed 16x

# Burn an ISO image to a blank CD-R or DVD-R
dotnet run --project src/Futureburn.Cli -- burn-iso ubuntu.iso F: --dry-run
dotnet run --project src/Futureburn.Cli -- burn-iso my-disc.iso F: --speed 8x --yes

# Salvage a partially-burned disc
dotnet run --project src/Futureburn.Cli -- finalize F

# GUI
dotnet run --project src/Futureburn.Gui
```

The GUI opens to a four-tile main window. **CD Info** is the live tile and shows real-time disc state, capabilities, and full TOC with track-by-track listings. The other three tiles are placeholders for now.

---

## Repository layout

```
futureburn/
├── Futureburn.sln
├── Directory.Build.props        # one place to bump version, set framework, etc.
├── CHANGELOG.md
├── LICENSE                       # MIT
└── src/
    ├── Futureburn.Core/
    │   ├── Imapi/                # IMAPI v2 + v1 typed COM, drive enum, disc inspection
    │   ├── Audio/                # NAudio wrappers, M3U parser, CdFormat constants
    │   └── Spti/                 # SCSI Pass-Through: native interop, MMC opcodes, burn engine
    ├── Futureburn.Cli/           # all the commands above
    └── Futureburn.Gui/           # WPF — MainWindow + CdInfoWindow + PlaceholderWindow
```

---

## Versioning

One number, one place — `<Version>` in `Directory.Build.props`. Every project picks it up. The CLI prints its version on every invocation; the GUI shows it in the title bar and About dialog.

Per-version changelog entries live in [CHANGELOG.md](./CHANGELOG.md).

---

## Contributing

Pull requests welcome. Issues welcome. If you have a quirky drive that doesn't work, open an issue with the output of `futureburn drives -v` and `futureburn imapi-v1-info` and we'll add a workaround.

If you build a feature, please match the existing tone in code comments — explanatory where the *why* isn't obvious (especially for SCSI/COM/IMAPI quirks) and otherwise let the names speak.

---

## License

[MIT](./LICENSE). Free for everyone, forever. Use it, fork it, ship it, sell hardware bundled with it. Just don't sell *the software itself* and pretend it's not free — there are too many people doing that already.

No warranty. No promises. Burns at your own risk.
