using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Futureburn.Core.Imapi;

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
            // Release the RCW promptly — leaving it to the GC can keep
            // the drive locked longer than we'd like.
            Marshal.FinalReleaseComObject(master);
        }
    }

    /// <summary>Find a drive by mount point ("F", "F:", "F:\") or by unique id.</summary>
    public static OpticalDrive? Find(string identifier)
    {
        var normalized = NormalizeMountPoint(identifier);
        return Enumerate().FirstOrDefault(d =>
            d.MountPoints.Any(m => string.Equals(m, normalized, StringComparison.OrdinalIgnoreCase))
            || string.Equals(d.UniqueId, identifier, StringComparison.OrdinalIgnoreCase));
    }

    internal static string NormalizeMountPoint(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.Length == 1 && char.IsLetter(s[0]))
            return $"{char.ToUpperInvariant(s[0])}:\\";
        if (s.Length == 2 && char.IsLetter(s[0]) && s[1] == ':')
            return $"{char.ToUpperInvariant(s[0])}:\\";
        if (s.Length == 3 && char.IsLetter(s[0]) && s[1] == ':' && s[2] == '\\')
            return $"{char.ToUpperInvariant(s[0])}:\\";
        return s;
    }

    private static OpticalDrive Inspect(Type recorderType, string uniqueId)
    {
        dynamic recorder = Activator.CreateInstance(recorderType)!;
        try
        {
            recorder.InitializeDiscRecorder(uniqueId);

            // Cast dynamic SAFEARRAYs to Array? so the helper calls below are
            // statically-resolved — passing them straight to a method that also
            // takes a method group (Mmc.LookupProfile) confuses the dynamic binder.
            return new OpticalDrive(
                UniqueId:              uniqueId,
                VendorId:              ((string)recorder.VendorId).Trim(),
                ProductId:             ((string)recorder.ProductId).Trim(),
                Revision:              ((string)recorder.ProductRevision).Trim(),
                MountPoints:           ToStringArray((Array?)recorder.VolumePathNames),
                CanLoadMedia:          (bool)recorder.DeviceCanLoadMedia,
                SupportedProfiles:     ToIntArray((Array?)recorder.SupportedProfiles).Select(Mmc.LookupProfile).ToArray(),
                CurrentProfiles:       ToIntArray((Array?)recorder.CurrentProfiles).Select(Mmc.LookupProfile).ToArray(),
                SupportedFeaturePages: ToIntArray((Array?)recorder.SupportedFeaturePages).Select(Mmc.LookupFeature).ToArray(),
                CurrentFeaturePages:   ToIntArray((Array?)recorder.CurrentFeaturePages).Select(Mmc.LookupFeature).ToArray());
        }
        finally
        {
            Marshal.FinalReleaseComObject(recorder);
        }
    }

    private static string[] ToStringArray(Array? safearray)
    {
        if (safearray is null) return Array.Empty<string>();
        return safearray.Cast<string>().ToArray();
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
