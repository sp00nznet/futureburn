# Data discs — ISO, folders, and BIN/CUE

Data discs are simpler than audio CDs in one way — the bytes on the disc are just
a filesystem image, no resampling, no Red Book timing — and more involved in
another: you have to *build* that filesystem image first.

## Burning an existing ISO

If you already have an `.iso`, `burn-iso` writes it straight to a blank disc. It
works on **CD-R and every common DVD type — DVD-R, DVD+R, DVD-RW, DVD+RW** — via
the raw SPTI data engine (`SptiDataBurner`).

```powershell
futureburn burn-iso ubuntu.iso F: --dry-run
futureburn burn-iso ubuntu.iso F: --speed 8x --yes
```

An ISO is already a sector-aligned filesystem image, so burning it is mostly a
long run of `WRITE 12` commands followed by finalization.

### Trusting "did it finalize?"

Early on, the data burner trusted the return code of `CLOSE SESSION`. That was a
mistake — some drives report success on the IOCTL while the session is still
open. The burner now **polls `READ DISC INFORMATION`** afterwards and confirms
the disc actually reports itself finalized before declaring victory.

## Folder → ISO

Most of the time you don't have an ISO — you have a folder of files. `mkiso`
builds an image; `burn-folder` builds it and burns it in one step.

```powershell
futureburn mkiso "C:\my-files" out.iso --label MYDISC
futureburn burn-folder "C:\my-files" F: --label MYDISC --speed 8x
```

`FsImageBuilder` (in `Futureburn.Core/Fs/`) drives **IMAPI2FS**, Windows' built-in
filesystem-image component, to produce an image carrying **three filesystems at
once**:

- **ISO 9660** — the lowest common denominator; readable everywhere.
- **Joliet** — adds long filenames and Unicode for Windows.
- **UDF** — handles large files and modern metadata.

All three describe the same files, so the disc mounts cleanly on anything from a
DOS box to a current Mac.

> **One sharp edge:** IMAPI2FS defaults its free-space estimate to *CD* capacity.
> Build a DVD-sized folder image without overriding it and the image is silently
> capped. `FsImageBuilder` sets `FreeMediaBlocks` to a large value so a folder
> bigger than a CD still images correctly.

## BIN/CUE

A `.cue` + `.bin` pair is the classic disc-image format. Hand `burn-iso` a `.cue`
and futureburn handles it:

1. `CueSheetParser` reads the `.cue` and finds the referenced `.bin`.
2. The sector mode is read from the cue — **MODE1/2048** (pure user data) or
   **MODE1/2352** (full raw sectors including sync and ECC).
3. `BinCueImageStream` exposes the **2,048-byte user-data portion** of each
   sector as a clean stream — for a MODE1/2352 bin, it strips the 16-byte sync
   header and the trailing error-correction bytes on the fly.
4. That stream is burned as a normal 2,048-byte-sector data disc.

So whether the bin is "cooked" or "raw," the bytes that land on the disc are the
same user data.

## Inspecting and validating

```powershell
futureburn cd-info F                 # drive + disc state, finalization, full TOC
futureburn validate-folder "C:\rip"  # is this a valid disc-structure folder?
```

`validate-folder` (backed by `DiscFolderValidator`) inspects a folder and tells
you whether it's a well-formed **DVD-Video, DVD-Audio, VCD, SVCD, Blu-ray Movie,**
or just plain data — and flags missing required files (a `VIDEO_TS.BUP` with no
`VIDEO_TS.IFO`, a VCD with no `AVSEQ*.DAT`, ...) *before* you burn a malformed
structure onto a disc you can't reuse. The same logic powers the disc-type label
in `cd-info`.
