# CD-Text and gapless — built, but not yet burnable

This is the one documented place where futureburn has a feature that is *finished
in code* but cannot be *exercised on the test hardware*. Worth explaining
honestly, because the code is there and the next person will wonder why it's
behind a flag.

## What CD-Text is

CD-Text stores artist, album, and per-track titles in the disc's **lead-in** —
the spiral region before track 1. A car stereo that reads CD-Text shows
"Whatever You Want" instead of "Track 16."

## What's done

The **encoder is complete and correct.** `Futureburn.Core/Spti/SptiCdText.cs`
produces fully spec-compliant CD-Text:

- 18-byte packs;
- the CCITT CRC-16 over each pack;
- the mandatory `0x8F` size-information packs;
- the libburn-style lead-in transport, wired into the SAO burn.

It's unit-tested, and you can preview the exact bytes offline — no disc, no
drive — with:

```powershell
futureburn cdtext-dump mix.m3u8
```

This has been verified against a real 19-track album: 52 packs, every CRC valid.

## Why it can't burn (yet)

CD-Text lives in the lead-in, and **only SAO/DAO recording** — Session-At-Once or
Disc-At-Once — lets the host populate the lead-in. SAO requires the drive to
accept the MMC **`SEND CUE SHEET`** command (opcode `0x5D`): the cue sheet
describes the whole disc layout up front so the drive can write the lead-in.

The test-bench drive — an **LG GE20LU10** — **rejects `SEND CUE SHEET`
outright.** Every cue sheet it's handed comes back with SCSI sense
`0x5 / 0x26 / 0x00` (invalid field in parameter list), including a minimal
single-track one. The drive simply does not implement DAO/SAO cue-sheet
recording.

To prove this wasn't a futureburn bug, there's a dedicated diagnostic:

```powershell
futureburn cuesheet-probe F
```

It submits **seven structurally different cue sheets**. On the GE20LU10 all
seven are rejected. The conclusion is the drive, not the code.

> Probing costs nothing in media — `SEND CUE SHEET` only *describes* a burn, it
> doesn't write anything. Failed cue-sheet attempts consume no CD-R.

## The same wall blocks `--gapless`

True **gapless** audio — no two-second silence between tracks — also needs
Disc-At-Once, for the same reason: only DAO lets the host control the gaps. So
`--gapless` is in exactly the same position as CD-Text. Built, correct, blocked
on the same missing drive capability.

## A real bug found along the way

Building the SAO path did flush out a genuine bug. `SptiCueSheet.cs` — the cue
sheet builder — had three format errors:

1. fields were **BCD-encoded** when they must be plain binary;
2. it emitted **A0/A1/A2 pointer descriptors**, which a cue sheet does not carry;
3. the **DATA FORM** byte on the lead-in and lead-out was wrong (must be `0x01`).

All three are fixed. The builder now matches `cdrecord` and `libburn`
byte-for-byte, verified against an MMC-5 worked example. The output is
spec-correct — it just can't be validated on a drive that refuses cue sheets at
all.

## What unblocks it

A different CD writer — one that actually supports SAO. The test:

```powershell
futureburn cuesheet-probe <new-drive>
```

If any cue sheet is **accepted**, the SAO path is live, and CD-Text + gapless can
finally be hardware-tested. The first unproven step after `SEND CUE SHEET`
succeeds would be the CD-Text lead-in *write* itself (negative-LBA, 96-byte
blocks).

Until then: the encoder is trustworthy, `cdtext-dump` lets you see its output,
and the flags stay marked experimental.
