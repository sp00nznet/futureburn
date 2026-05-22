# LightScribe labels

LightScribe was a mid-2000s technology for *etching* a label onto the top side of
a specially-coated disc — using the same laser that burns the data side. You burn
your music, flip the disc, and the drive lasers a greyscale image into the
label coating. No printer, no sticker.

Getting this working in futureburn was, in the project's own words, the white
whale. It works now.

## How it works

The pieces live in `Futureburn.Core/LightScribe/`.

1. **Find a capable drive.** `lightscribe-info` enumerates optical drives and
   reports which ones are LightScribe-capable.
2. **Convert the image.** Any PNG / JPG / BMP you supply is converted to a
   **24-bit, centre-fit BMP** — LightScribe's print API is particular about the
   pixel format and wants the artwork centred on the disc's printable annulus.
3. **Submit to the LSS Public SDK.** futureburn calls into HP's
   `LSPrintAPI.dll` — the LightScribe System (LSS) public SDK — to hand the drive
   the image and a quality setting.
4. **The drive etches.** At **best** quality on the test-bench LG GE20LU10, a
   full label takes roughly **16 minutes**.

```powershell
futureburn lightscribe-info                       # which drives can do it
futureburn lightscribe-print F .\cover.png        # etch a label
```

Quality is `draft`, `normal`, or `best` — slower means darker and crisper.

## The 32-bit constraint

`LSPrintAPI.dll` is **32-bit only**. HP never shipped an x64 build, and a 64-bit
process cannot `LoadLibrary` a 32-bit DLL. This single fact is why both
futureburn executables are built `x86` — see [architecture.md](architecture.md).

## Current limitation: the LSS dialog

Today, `lightscribe-print` submits the job through LSS's **user-driven dialog** —
futureburn prepares everything and the LSS UI pops up for you to click "Print."
One click, then it runs unattended.

Fully *headless* submission — no dialog at all — is built but not yet working.
It's blocked on an undocumented detail of how the LSS SDK wants the drive
identified (a `boost::program_options`-style argument format that hasn't been
cracked). The dead-end map is recorded in the project memory; the productive next
step is to run Process Monitor against a known-good LSS job and watch what it
actually passes. Until then, the one-click dialog is the supported path.

## One-shot: music + label in one command

The original goal of the whole project was a single command that burns an audio
CD *and* its label. That command exists:

```powershell
futureburn burn mix.m3u8 F: --image cover.png
```

It burns the audio side, ejects the disc, walks you through flipping it — with a
sanity check that you actually *did* flip it — and then LightScribes the label.
The entire goal-stack, collapsed into one line.

## GUI

The **Burn Label (LightScribe)** tile mirrors the audio-CD tile: drag in an
image, pick a drive and a quality, click Burn. Live progress on a background
thread, same as every other burn in futureburn.
