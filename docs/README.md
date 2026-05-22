# futureburn documentation

How everything works, under the hood. If you just want to *use* futureburn, the
top-level [README](../README.md) and `futureburn help` cover that. These docs are
for the curious — and for anyone who wants to fork it and not be lost.

## Contents

| Doc | What it covers |
|---|---|
| [architecture.md](architecture.md) | The three projects, how they fit together, the dependency rules |
| [burn-engines.md](burn-engines.md) | IMAPI v2, IMAPI v1, and raw SPTI — the three ways we write a disc |
| [audio-cds.md](audio-cds.md) | Decoding, resampling, and writing a Red Book audio CD |
| [data-discs.md](data-discs.md) | ISO images, folder → ISO, and BIN/CUE burning |
| [video-discs.md](video-discs.md) | The MKV → DVD-Video pipeline, menus, DVD-Audio, and VCD |
| [lightscribe.md](lightscribe.md) | Etching artwork onto the disc label |
| [cd-text-and-gapless.md](cd-text-and-gapless.md) | Why CD-Text and gapless are built but not yet burnable |
| [cli-reference.md](cli-reference.md) | Every command, every flag |

## The 60-second tour

futureburn is one C# solution with three projects:

- **`Futureburn.Core`** — all the real logic. No UI. Talks to drives, decodes
  audio, builds filesystems, drives external tools.
- **`Futureburn.Cli`** — a thin command parser over Core.
- **`Futureburn.Gui`** — a WPF shell, also a thin layer over Core.

Everything interesting lives in Core. The CLI and GUI are two faces on the same
engine — anything one can do, the other can too.

A disc burn, at the lowest level, is just a sequence of SCSI MMC commands sent
to the drive. futureburn can send those three different ways (see
[burn-engines.md](burn-engines.md)); the raw SPTI engine is the one that does the
heavy lifting and the one we trust most.
