# Video discs

futureburn can author **DVD-Video**, **DVD-Audio**, and **Video CD** discs. The
headline feature is the MKV → DVD-Video pipeline: hand it a modern rip and get
back a disc that plays in a 2003 set-top DVD player.

## External tools

Video authoring is the one area futureburn doesn't do entirely itself —
re-implementing an MPEG-2 encoder and a DVD navigation-table compiler would be a
project each. Instead it orchestrates well-tested external tools and checks for
them up front:

| Tool | Role | Check command |
|---|---|---|
| `ffmpeg` / `ffprobe` | Transcoding and media inspection | `futureburn ffmpeg` |
| `dvdauthor` | DVD-Video IFO / navigation authoring | `futureburn dvdauthor` |
| `spumux` | Muxing subtitles into the DVD stream | (ships with dvdauthor) |
| `dvda-author` | DVD-Audio `AUDIO_TS` authoring | `futureburn dvda-author-info` |

The locators and runners live in `Futureburn.Core/Tools/`. A convenient way to
get `dvdauthor` + `spumux` together is `winget install AlexThuering.DVDStyler` —
DVDStyler bundles both.

## DVD-Video — the MKV pipeline

`dvdv-author` (driven by `MkvDvdPipeline`) takes any video file — MKV, MP4, AVI —
and carries the whole structure through to a hardware-playable disc.

```powershell
futureburn dvdv-author movie.mkv .\out
futureburn dvdv-author movie.mkv .\out --menu --burn F:
```

Stages:

1. **Transcode** — ffmpeg's `ntsc-dvd` / `pal-dvd` target preset re-encodes the
   video to spec-compliant MPEG-2 + audio.
2. **Carry the structure across** — this is the part cheap converters drop:
   - **Chapter markers** in the source become real chapter stops on the disc.
   - **Every audio track** comes along, each with its language label.
   - **Subtitles** ride too — text subtitles are rendered with `spumux`; bitmap
     subtitles (VobSub, PGS) are re-encoded into the DVD subpicture format.
3. **Author the IFOs** — `dvdauthor` builds the real navigation tables, so the
   disc plays on standalone hardware. If `dvdauthor` isn't installed,
   `DvdIfoBuilder` writes skeleton IFOs instead — those play in VLC but not in a
   set-top player. Install `dvdauthor` for real discs.
4. **Burn** — with `--burn <drive>`, the result is imaged to UDF and written to
   the disc. Transcode → author → image → burn, one command.

This path is hardware-validated — a Wallace & Gromit MKV authored, burned, and
confirmed playing on a real DVD player.

## DVD menus

By default a `dvdv-author` disc auto-plays the movie. Add `--menu` (or tick the
**DVD menu** box on the Burn Blu-ray / DVD tile) and `DvdMenuBuilder` authors a
navigable disc instead:

- a **root menu** with **Play Movie** and **Scene Selection**;
- a **scene menu** with one button per chapter.

futureburn renders the menu artwork, builds the button-highlight subpictures, and
wires the remote-control navigation. If the source has no chapter marks, it
generates them at sensible intervals so Scene Selection still has something to
point at.

> **An authoring gotcha worth recording:** in the dvdauthor XML, a title's
> end-of-playback `<post>` block must `call` the menu, not `jump` to it. Use
> `jump` and dvdauthor rejects it — "cannot jump to a menu from a title." `call`
> remembers where it came from; `jump` doesn't.

## DVD-Audio

`dvda-author` builds a high-resolution **DVD-Audio** disc:

```powershell
futureburn dvda-author playlist.m3u8 .\out
```

Each track is decoded to LPCM WAV (16-bit / 44.1 kHz) and handed to the external
`dvda-author` tool, which produces spec-compliant `AUDIO_TS\` IFO / BUP / AOB
files.

> DVD-Audio is a niche format. A PS4 and most modern players **cannot** read it —
> playback needs a DVD-A-aware player, such as some early-2000s premium car
> audio systems and home-theater receivers.

## Video CD (experimental)

`vcd-author` takes a video file, runs ffmpeg's `pal-vcd` / `ntsc-vcd` preset to
produce MPEG-1 + MP2 in an MPEG program stream, and writes the binary `INFO.VCD`
and `ENTRIES.VCD` control files for the standard VCD folder layout.

Software players (VLC, MPC-HC) play the result. Strict standalone VCD players may
reject it: a real VCD is a multi-track disc, and futureburn currently burns
single-track data CDs. Proper multi-track VCD mastering is a separate future
project — hence "experimental."
