using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Futureburn.Core.Imapi;

[SupportedOSPlatform("windows")]
public static class DiscInspector
{
    public sealed class NoMediaException : Exception
    {
        public NoMediaException(string message) : base(message) { }
    }

    /// <summary>
    /// Inspect what's loaded in the drive identified by mount point or unique id.
    /// Returns null if the drive isn't found.
    /// Throws NoMediaException if the drive has no disc.
    /// </summary>
    public static LoadedDisc? Inspect(string identifier)
    {
        var drive = DriveEnumerator.Find(identifier);
        return drive is null ? null : InspectDrive(drive);
    }

    public static LoadedDisc InspectDrive(OpticalDrive drive)
    {
        int profileCode = drive.CurrentProfiles.FirstOrDefault(p => p.Code != 0)?.Code ?? 0;
        if (profileCode == 0)
            throw new NoMediaException($"No disc in {drive.PrimaryMount ?? drive.UniqueId}.");

        var mediaType = Mmc.ProfileToMedia(profileCode);

        // MsftDiscFormat2Data fails for finalized discs, ROM media, audio CDs etc.
        // That's expected — we still know the media type from the drive's profile.
        var details = TryReadDataFormatDetails(drive);
        if (details is null)
        {
            return new LoadedDisc(
                MediaType:                mediaType,
                HasFormatDetails:         false,
                MediaPhysicallyBlank:     false,
                MediaHeuristicallyBlank:  false,
                TotalSectors:             0,
                FreeSectors:              0,
                NextWritableAddress:      0,
                CurrentWriteSpeedKbps:    0,
                SupportedWriteSpeedsKbps: Array.Empty<int>());
        }

        var (totalSec, freeSec, nextLba, writeSpeed, supportedSpeeds) = details.Value;
        bool inferredBlank = totalSec > 0 && freeSec == totalSec;

        return new LoadedDisc(
            MediaType:                mediaType,
            HasFormatDetails:         true,
            MediaPhysicallyBlank:     inferredBlank,
            MediaHeuristicallyBlank:  inferredBlank,
            TotalSectors:             totalSec,
            FreeSectors:              freeSec,
            NextWritableAddress:      nextLba,
            CurrentWriteSpeedKbps:    writeSpeed,
            SupportedWriteSpeedsKbps: supportedSpeeds);
    }

    // IMAPI2 quirk: the IDispatch on MsftDiscFormat2Data only exposes members
    // declared directly on IDiscFormat2Data — the inherited IDiscFormat2 base
    // members (CurrentMediaType, MediaPhysicallyBlank, etc.) aren't reachable
    // via dynamic. We work with what's directly available, and infer blank
    // from FreeSectors == TotalSectors. If we ever need authoritative state,
    // declare a typed [ComImport] IDiscFormat2 interface and cast.
    private static (long totalSec, long freeSec, long nextLba, int writeSpeed, int[] supportedSpeeds)?
        TryReadDataFormatDetails(OpticalDrive drive)
    {
        var recorderType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")
            ?? throw new InvalidOperationException("IMAPI2 recorder COM class missing.");
        var formatType = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Data")
            ?? throw new InvalidOperationException("IMAPI2 data-format COM class missing.");

        dynamic recorder = Activator.CreateInstance(recorderType)!;
        try
        {
            recorder.InitializeDiscRecorder(drive.UniqueId);

            dynamic format = Activator.CreateInstance(formatType)!;
            try
            {
                try { format.Recorder = recorder; }
                catch (COMException) { return null; }

                try
                {
                    long totalSec  = Convert.ToInt32(format.TotalSectorsOnMedia);
                    long freeSec   = Convert.ToInt32(format.FreeSectorsOnMedia);
                    long nextLba   = Convert.ToInt32(format.NextWritableAddress);
                    int writeSpeed = Convert.ToInt32(format.CurrentWriteSpeed);
                    int[] supportedSpeeds = ToIntArray((Array?)format.SupportedWriteSpeeds);
                    return (totalSec, freeSec, nextLba, writeSpeed, supportedSpeeds);
                }
                catch (COMException) { return null; }
            }
            finally { Marshal.FinalReleaseComObject(format); }
        }
        finally { Marshal.FinalReleaseComObject(recorder); }
    }

    private static int[] ToIntArray(Array? safearray)
    {
        if (safearray is null) return Array.Empty<int>();
        var result = new int[safearray.Length];
        for (int i = 0; i < safearray.Length; i++)
            result[i] = Convert.ToInt32(safearray.GetValue(i));
        return result;
    }
}
