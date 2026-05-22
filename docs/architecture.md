# Architecture

futureburn is a single .NET 8 solution, `Futureburn.sln`, with three projects.

```
Futureburn.Gui  ─┐
                 ├─► Futureburn.Core   (all the real work)
Futureburn.Cli  ─┘
```

The rule is simple: **Core does the work, the CLI and GUI just present it.** No
disc logic, no SCSI, no file-format code ever lives in the CLI or GUI. If you
find yourself writing a `WRITE 12` CDB in `MainWindow.xaml.cs`, stop — it belongs
in Core.

## Futureburn.Core

A class library. No `Main`, no UI, no `Console.WriteLine` in the hot paths. It is
organised by concern:

| Folder | Responsibility |
|---|---|
| `Imapi/` | IMAPI v2 + v1 — Windows' built-in COM burning APIs, wrapped in hand-rolled `[ComImport]` interfaces. Also drive enumeration and disc inspection. |
| `Spti/` | SCSI Pass-Through. The native interop (`SptiNative`), the MMC opcode tables (`MmcOpcodes`), a device handle (`SptiDevice`), and the burn engines — `SptiAudioCdBurner`, `SptiDataBurner`, plus `SptiCdText` and `SptiCueSheet`. |
| `Audio/` | NAudio wrappers for decoding any input format, the M3U/M3U8 playlist parser, and Red Book CD-format constants (44.1 kHz / 16-bit / stereo, 2352-byte sectors). |
| `Fs/` | Building disc images — `FsImageBuilder` produces ISO 9660 + Joliet + UDF via IMAPI2FS. `DiscFolderValidator` recognises disc-structure folders (VIDEO_TS, AUDIO_TS, ...). |
| `Image/` | BIN/CUE — `CueSheetParser` reads a `.cue`, `BinCueImageStream` exposes the user-data portion of the `.bin` as a burnable stream. |
| `Authoring/` | Video-disc authoring — `MkvDvdPipeline`, `DvdIfoBuilder`, `DvdMenuBuilder`, and the VCD `INFO.VCD` / `ENTRIES.VCD` builders. |
| `Ffmpeg/` | Finding and running ffmpeg / ffprobe. |
| `Tools/` | Locators and runners for the other external tools — `dvdauthor`, `spumux`, `dvda-author` — plus ISO language-code helpers. |
| `LightScribe/` | Interop with HP's `LSPrintAPI.dll` and label-image conversion. |
| `Net/` | The MusicBrainz client — disc-ID computation and the fuzzy TOC fallback search. |

## Futureburn.Cli

One file, `Program.cs`. It prints the version banner, then a single `switch` on
`args[0]` dispatches to a handler method. Each handler parses its own flags and
calls into Core. That's the whole CLI. See [cli-reference.md](cli-reference.md).

## Futureburn.Gui

A WPF app — a four-tile main window and four feature windows (Burn Audio CD,
Burn Blu-ray / DVD, CD Info, Burn Label). Every window runs Core work on a
background thread and reports progress back to the UI thread.

Two GUI-specific wrinkles worth knowing:

- **`FileDialogs.cs`** — file/folder pickers are opened on a dedicated STA
  thread. On an x86 WPF process, opening a second common dialog directly on the
  UI thread could hang the app; the STA-thread helper sidesteps that.
- **The mascot icon** — `app.ico` is wired in via `<ApplicationIcon>` in the
  `.csproj`. It becomes the executable icon, and WPF windows fall back to it for
  their title-bar and taskbar icon.

## Why x86

`Directory.Build.props` targets `net8.0-windows` for every project, but the two
**executables** (`Futureburn.Cli`, `Futureburn.Gui`) override `PlatformTarget`
to `x86`. The reason is LightScribe: HP's `LSPrintAPI.dll` is 32-bit only and was
never shipped as x64, so a 64-bit process cannot `LoadLibrary` it. The library
projects (`Core`, the test project) stay AnyCPU — P/Invoke is resolved lazily,
so the test runner never touches the 32-bit DLL and can keep using the x64
runtime.

## Versioning

One `<Version>` in `Directory.Build.props`. Every project inherits it. Bump it
there, add a `CHANGELOG.md` entry, done.
