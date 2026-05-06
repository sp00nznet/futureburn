using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Futureburn.Core.Imapi;

// Hand-rolled IMAPI2 access via late-bound COM. No NuGet wrapper.
//
// IMAPI2 = Image Mastering API v2. It's the COM-based optical-disc API that
// ships with every modern Windows. We grab the COM type by ProgID, activate
// it, and dispatch through `dynamic` so we don't have to declare any
// [ComImport] interfaces ourselves. Cheap, cheerful, and Windows-only.

[SupportedOSPlatform("windows")]
public static class DriveEnumerator
{
    public static IReadOnlyList<OpticalDrive> Enumerate()
    {
        var masterType = Type.GetTypeFromProgID("IMAPI2.MsftDiscMaster2")
            ?? throw new InvalidOperationException(
                "IMAPI2 isn't available on this system. Are we even on Windows?");

        var recorderType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")
            ?? throw new InvalidOperationException("IMAPI2 recorder COM class missing.");

        dynamic master = Activator.CreateInstance(masterType)!;
        try
        {
            var drives = new List<OpticalDrive>();
            int count = master.Count;
            for (int i = 0; i < count; i++)
            {
                string uniqueId = master[i];
                drives.Add(Inspect(recorderType, uniqueId));
            }
            return drives;
        }
        finally
        {
            // Release the RCW promptly — leaving it to the GC can keep the
            // drive locked longer than we'd like.
            Marshal.FinalReleaseComObject(master);
        }
    }

    private static OpticalDrive Inspect(Type recorderType, string uniqueId)
    {
        dynamic recorder = Activator.CreateInstance(recorderType)!;
        try
        {
            recorder.InitializeDiscRecorder(uniqueId);

            // VolumePathNames is a SAFEARRAY of BSTR — drive letters / mount points.
            var mountPoints = ((Array)recorder.VolumePathNames)
                .Cast<string>()
                .ToArray();

            return new OpticalDrive(
                UniqueId: uniqueId,
                VendorId: ((string)recorder.VendorId).Trim(),
                ProductId: ((string)recorder.ProductId).Trim(),
                Revision: ((string)recorder.ProductRevision).Trim(),
                MountPoints: mountPoints);
        }
        finally
        {
            Marshal.FinalReleaseComObject(recorder);
        }
    }
}

public sealed record OpticalDrive(
    string UniqueId,
    string VendorId,
    string ProductId,
    string Revision,
    IReadOnlyList<string> MountPoints);
