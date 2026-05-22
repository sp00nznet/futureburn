# Burn engines

Writing to an optical drive on Windows can happen at three different levels of
the stack. None of them works on every drive — old USB writers in particular are
fussy. futureburn implements all three and lets you pick with `--engine`.

```
your audio / data
        │
        ▼
┌───────────────────────────────────────────┐
│  --engine v2   IMAPI 2   (modern COM)      │
│  --engine v1   IMAPI 1   (legacy COM)      │
│  --engine spti Raw SPTI  (SCSI MMC)        │
└───────────────────────────────────────────┘
        │
        ▼
   the optical drive
```

## `--engine v2` — IMAPI 2 (default)

The modern Windows burning API, introduced with Vista. We talk to it through
hand-rolled `[ComImport]` interfaces — `MsftDiscFormat2TrackAtOnce` and friends —
rather than a NuGet wrapper, so there are no surprise dependencies and we can see
exactly which COM calls go out.

This is the default because on most drives made in the last fifteen years it
simply works. Windows handles the staging, the lead-in/lead-out, the
finalization. Reach for the other engines only when it doesn't.

## `--engine v1` — IMAPI 1 (legacy)

The XP-era COM API — `MsDiscMasterObj` plus `IRedbookDiscMaster`. Ancient, but
still present in Windows 11. Some genuinely old drives respond to v1 when v2
returns cryptic failures like "mode page not present."

Run `futureburn imapi-v1-info` to check whether v1 is even functional on your
machine before relying on it.

## `--engine spti` — raw SCSI Pass-Through

This is the interesting one. SPTI — SCSI Pass-Through Interface — lets us hand
the drive raw **MMC command descriptor blocks (CDBs)** ourselves, through the
`IOCTL_SCSI_PASS_THROUGH_DIRECT` device control code. No middle layer. The same
approach ImgBurn uses.

This is the engine that wrote futureburn's first real audio CD, on a 2008-vintage
USB writer that both IMAPI paths refused to drive. When a drive is uncooperative,
SPTI is the answer, because we control every byte.

The pieces, all in `Futureburn.Core/Spti/`:

- **`SptiNative`** — the P/Invoke surface: `CreateFile` on `\\.\X:`,
  `DeviceIoControl`, and the `SCSI_PASS_THROUGH_DIRECT` struct layout.
- **`SptiDevice`** — a thin handle wrapper; opens the drive, sends a CDB, and
  hands back the data and the SCSI sense bytes.
- **`MmcOpcodes`** — the MMC command set: `INQUIRY`, `READ DISC INFORMATION`,
  `READ TOC`, `GET CONFIGURATION`, `WRITE 12`, `CLOSE TRACK/SESSION`,
  `SEND CUE SHEET`, and the rest.
- **`SptiAudioCdBurner`** / **`SptiDataBurner`** — the engines that sequence
  those opcodes into a complete, finalized disc.

### Reading sense data

Every SCSI command can fail, and when it does the drive returns **sense data** —
a key/ASC/ASCQ triple that says *why*. futureburn reads and reports it, because
"the burn failed" is useless and "sense 0x5/0x26/0x00 — invalid field in
parameter list" tells you exactly what to fix. Most of the hard-won bug fixes in
this project started with a sense code.

## Picking an engine

You usually don't have to think about it — `v2` is the default and is fine. If a
burn fails, the diagnostics tell you what your hardware supports:

```powershell
futureburn drives -v          # capabilities of every drive
futureburn imapi-v1-info      # is IMAPI v1 alive on this PC?
futureburn spti-info F        # does raw pass-through work on F:?
```

Then `burn ... --engine spti` (or `v1`) to switch.
