# Audio CDs

futureburn burns **Red Book** audio CDs — the original 1980 CD-DA standard. A
Red Book disc is rigidly specified, and that rigidity is what makes a burned CD
play in a 1994 boombox:

- **44,100 Hz** sample rate
- **16-bit** signed samples
- **stereo** (2 channels)
- **2,352-byte** sectors — exactly 1/75th of a second of audio

Anything you feed in has to come out the other end in exactly that shape.

## The pipeline

```
your files (MP3/FLAC/WAV/...)  ─►  decode  ─►  resample to 44.1k/16/stereo
                                                       │
        finalized audio CD  ◄─  WRITE 12 sectors  ◄─  pad to sector boundary
```

### 1. Input — files and playlists

You can hand `burn` individual audio files or an **M3U / M3U8 playlist**. The
parser in `Futureburn.Core/Audio/` handles both plain M3U and extended M3U
(`#EXTM3U` with `#EXTINF:` duration/title lines). Playlist order is track order.

### 2. Decode

Decoding goes through **NAudio**, which leans on Windows Media Foundation. That
means anything Windows can play, futureburn can burn: **WAV, MP3, M4A, AAC, WMA,
FLAC**. Each file is decoded to raw PCM samples.

### 3. Resample

A 48 kHz FLAC or a 22 kHz mono MP3 is not Red Book. Every track is resampled and
reformatted to exactly 44.1 kHz / 16-bit / stereo. After this step, every track
is byte-compatible with every other and with the CD sector layout.

### 4. Pad to the sector boundary

A track almost never ends exactly on a 2,352-byte sector boundary. The drive
writes whole sectors only, so each track's final partial sector — and final
partial *write chunk* — is padded with zero samples (digital silence).

This sounds trivial. It was not. See below.

### 5. Write

With the SPTI engine, each track is streamed to the drive with `WRITE 12` CDBs,
then `CLOSE TRACK` between tracks, and finally `CLOSE SESSION` to finalize the
disc so it plays everywhere. The data engines on the IMAPI side let Windows
sequence this instead.

## The multi-track bug (and the fix)

The first single-track burns worked early. Multi-track did not, and it took six
CD-Rs to understand why.

Symptom: burns died partway through **track 2** with SCSI sense `0x29` — UNIT
ATTENTION, "the device was reset."

Cause: the **trailing partial chunk** of a track. The final `WRITE 12` of a
track was sending a CDB whose transfer-length field didn't match the
`DataTransferLength` we'd put in the `SCSI_PASS_THROUGH_DIRECT` struct. The drive
saw a malformed transfer, the USB Bulk-Only Transport layer triggered a recovery
reset, and that reset surfaced — confusingly, several commands later — as a unit
attention mid-track.

Fix: **pad every track's final write chunk up to the full chunk-size boundary**
with zero PCM, and set `DataTransferLength` to match exactly. This is precisely
what cdrtools and libburn do. The cost is under 100 ms of inaudible silence at
track ends; the benefit is a 19-track, hour-long album that burns end to end and
seeks cleanly in foobar2000.

The lesson generalised into a project rule: when a transfer length is involved,
the CDB and the IOCTL struct must agree to the byte.

## Diagnostics

```powershell
futureburn probe song.flac          # codec, sample rate, channels, tags
futureburn decode song.mp3 out.wav  # decode to a Red Book WAV without burning
futureburn playlist mix.m3u8        # show the parsed playlist and total time
futureburn burn mix.m3u8 F: --dry-run   # full run, no actual writes
```

`--dry-run` walks the entire pipeline and stops just short of writing — the
fastest way to confirm your inputs are sane before committing a disc.
