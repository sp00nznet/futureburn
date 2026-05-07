namespace Futureburn.Core.Image;

// Parsed representation of a CD/DVD .cue sheet (the text form used by
// BIN/CUE pairs). NOT the same as the binary cue-sheet IMAPI/SCSI uses
// for SAO disc burning — that one's in Spti/SptiCueSheet.cs.
//
// Reference: https://en.wikipedia.org/wiki/Cue_sheet_(computing)#File_format

public sealed record CueTrack(
    int Number,
    CueTrackMode Mode,
    int SectorBytes,
    long IndexZeroLba,    // pre-gap start LBA (0 if no INDEX 00 line)
    long IndexOneLba);    // track-body start LBA

public enum CueTrackMode
{
    Audio,        // 2352-byte raw audio
    Mode1,        // CD-ROM data, Form 1
    Mode2,        // CD-ROM data, Form 2 (typically used for VCD/SVCD)
    Unknown,
}

public sealed record CueSheet(
    string SourcePath,
    string BinFile,         // resolved absolute path
    string BinFormat,       // BINARY / WAVE / etc.
    IReadOnlyList<CueTrack> Tracks)
{
    public bool IsSingleDataTrack =>
        Tracks.Count == 1
        && (Tracks[0].Mode == CueTrackMode.Mode1 || Tracks[0].Mode == CueTrackMode.Mode2);

    public bool IsAllAudio =>
        Tracks.Count > 0 && Tracks.All(t => t.Mode == CueTrackMode.Audio);
}
