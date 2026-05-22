# CLI reference

Every command futureburn understands. Run `futureburn` with no arguments for the
built-in usage summary, or `futureburn help`.

The version banner prints on every invocation. A drive is given by its letter —
`F`, `F:`, or `\\.\F:` all work.

```
futureburn <command> [arguments] [flags]
```

## Drives and discs

| Command | What it does |
|---|---|
| `drives` (`-d`) | List optical drives. Add `-v` / `--verbose` for the full capability dump — profiles, feature pages, supported speeds. |
| `disc <drive>` | Inspect the disc in a drive. |
| `cd-info <drive>` | Drive + disc state: capabilities, what's loaded, finalization status, a one-line suggested action, and the complete TOC. |
| `cd-lookup <drive>` | Compute the MusicBrainz disc ID from the disc's TOC and query the public database. Falls back to a fuzzy TOC search when there's no exact disc-ID match. |
| `eject <drive>` | Open the tray. |
| `load <drive>` | Close the tray. |

## Audio

| Command | What it does |
|---|---|
| `probe <file>` | Show a file's format — codec, sample rate, channels, duration, tags. Richer when ffmpeg is installed. |
| `decode <in> <out.wav>` | Decode any supported audio file to a Red Book WAV (44.1 kHz / 16-bit / stereo). |
| `playlist <m3u>` | Parse and display an M3U / M3U8 playlist with total running time. |
| `mkplaylist <files...>` | Build a playlist file from a set of audio files. |

## Burning

| Command | What it does |
|---|---|
| `burn <playlist> <drive>` | Burn an audio CD from a playlist or files. |
| `burn-iso <image> <drive>` | Burn an existing `.iso` (or a `.cue`/BIN-CUE pair) to a blank disc. |
| `mkiso <folder> <out.iso>` | Build an ISO 9660 + Joliet + UDF image from a folder. |
| `burn-folder <folder> <drive>` | Build the image and burn it, in one step. |
| `finalize <drive>` | Salvage a partially-burned disc by closing its open session. |

### Common burn flags

| Flag | Effect |
|---|---|
| `--dry-run` | Run the whole pipeline but stop before writing. |
| `--speed <N>x` | Request a write speed, e.g. `--speed 16x`. |
| `--engine <v2\|v1\|spti>` | Choose the burn engine. Default `v2`. See [burn-engines.md](burn-engines.md). |
| `--yes` | Skip the confirmation prompt. |
| `--force` | Proceed past non-fatal warnings. |
| `--keep-temp` | Don't delete intermediate / staged files. |
| `--label <name>` | Volume label for `mkiso` / `burn-folder`. |
| `--image <picture>` | Burn audio, then LightScribe this artwork onto the label (one-shot labeled CD). |
| `--cdtext --album "..." --artist "..."` | Encode CD-Text into the lead-in. **Needs an SAO-capable drive** — see [cd-text-and-gapless.md](cd-text-and-gapless.md). |
| `--gapless` | True Disc-At-Once gapless burn. **Needs an SAO-capable drive** (same as above). |

## Video and audio discs

| Command | What it does |
|---|---|
| `dvdv-author <input> <outdir>` | Author a DVD-Video from any video file. `--menu` adds a navigable menu; `--burn <drive>` writes the disc. |
| `dvda-author <playlist\|folder> <outdir>` | Author a high-resolution DVD-Audio disc. |
| `vcd-author <input> <outdir>` | Author a Video CD (experimental). |
| `validate-folder <folder>` | Identify and validate a disc-structure folder — DVD-Video, DVD-Audio, VCD, SVCD, Blu-ray, or data. |

## LightScribe

| Command | What it does |
|---|---|
| `lightscribe-info` | List LightScribe-capable drives. |
| `lightscribe-print <drive> <image>` | Etch an image onto the disc label. Quality: `draft`, `normal`, `best`. |

## Diagnostics

| Command | What it does |
|---|---|
| `imapi-v1-info` | Report whether legacy IMAPI v1 is functional on this PC. |
| `spti-info <drive>` | Report whether raw SCSI pass-through works on a drive. |
| `cuesheet-probe <drive>` | Submit seven cue-sheet variants to test whether a drive supports SAO recording. Writes nothing — consumes no media. |
| `cdtext-dump <playlist>` | Print the encoded CD-Text packs offline, no disc needed. |
| `ffmpeg` | Locate ffmpeg / ffprobe and report status. |
| `dvdauthor` | Check for `dvdauthor` and print install instructions. |
| `dvda-author-info` | Check for the external `dvda-author` tool. |

## Examples

```powershell
# Burn a 16x audio CD with the raw SCSI engine
futureburn burn mix.m3u8 F: --engine spti --speed 16x

# Audio CD plus a LightScribe label, one command
futureburn burn mix.m3u8 F: --image cover.png

# MKV to a menu'd, hardware-playable DVD, burned
futureburn dvdv-author movie.mkv .\out --menu --burn F:

# Build an ISO from a folder and burn it
futureburn burn-folder "C:\my-files" F: --label MYDISC --speed 8x

# Will my drive ever do CD-Text? (writes nothing)
futureburn cuesheet-probe F
```
