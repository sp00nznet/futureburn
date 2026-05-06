namespace Futureburn.Core.Imapi;

// MMC (Multi-Media Command) profile + feature codes, plus the IMAPI2
// physical media type enum, with human-readable names.
//
// When IMAPI2 reports a profile or feature code we don't recognize, we
// surface it as raw hex with an "Unknown" label rather than dropping it —
// so weird hardware (Xbox 360 DVD drives, HD-DVD, exotic Blu-ray formats)
// stays visible even before we add a friendly name for it.

public static class Mmc
{
    public enum ProfileCategory { None, Cd, Dvd, BluRay, HdDvd, MagnetoOptical, Disk, Other }

    public sealed record ProfileInfo(int Code, string Name, ProfileCategory Category, bool Writable)
    {
        public string HexCode => $"0x{Code:X4}";
    }

    public sealed record FeatureInfo(int Code, string Name)
    {
        public string HexCode => $"0x{Code:X4}";
    }

    // IMAPI_MEDIA_PHYSICAL_TYPE — what kind of disc is currently loaded.
    public enum MediaPhysicalType
    {
        Unknown = 0,
        CdRom = 1, CdR = 2, CdRw = 3,
        DvdRom = 4, DvdRam = 5,
        DvdPlusR = 6, DvdPlusRw = 7, DvdPlusRDualLayer = 8,
        DvdDashR = 9, DvdDashRw = 10, DvdDashRDualLayer = 11,
        RandomAccessDisk = 12,
        DvdPlusRwDualLayer = 13,
        HdDvdRom = 14, HdDvdR = 15, HdDvdRam = 16,
        BdRom = 17, BdR = 18, BdRe = 19,
    }

    public static string MediaName(MediaPhysicalType mt) => mt switch
    {
        MediaPhysicalType.Unknown => "Unknown",
        MediaPhysicalType.CdRom => "CD-ROM",
        MediaPhysicalType.CdR => "CD-R",
        MediaPhysicalType.CdRw => "CD-RW",
        MediaPhysicalType.DvdRom => "DVD-ROM",
        MediaPhysicalType.DvdRam => "DVD-RAM",
        MediaPhysicalType.DvdPlusR => "DVD+R",
        MediaPhysicalType.DvdPlusRw => "DVD+RW",
        MediaPhysicalType.DvdPlusRDualLayer => "DVD+R DL",
        MediaPhysicalType.DvdDashR => "DVD-R",
        MediaPhysicalType.DvdDashRw => "DVD-RW",
        MediaPhysicalType.DvdDashRDualLayer => "DVD-R DL",
        MediaPhysicalType.RandomAccessDisk => "Random-access disk",
        MediaPhysicalType.DvdPlusRwDualLayer => "DVD+RW DL",
        MediaPhysicalType.HdDvdRom => "HD DVD-ROM",
        MediaPhysicalType.HdDvdR => "HD DVD-R",
        MediaPhysicalType.HdDvdRam => "HD DVD-RAM",
        MediaPhysicalType.BdRom => "BD-ROM",
        MediaPhysicalType.BdR => "BD-R",
        MediaPhysicalType.BdRe => "BD-RE",
        _ => $"Unknown media type 0x{(int)mt:X4}",
    };

    // Map an MMC profile code to the closest IMAPI media type.
    // Used when we can't read CurrentMediaType from the data-format object directly
    // (its IDispatch doesn't expose the inherited IDiscFormat2 members).
    public static MediaPhysicalType ProfileToMedia(int profileCode) => profileCode switch
    {
        0x0008 => MediaPhysicalType.CdRom,
        0x0009 => MediaPhysicalType.CdR,
        0x000A => MediaPhysicalType.CdRw,
        0x0010 => MediaPhysicalType.DvdRom,
        0x0011 => MediaPhysicalType.DvdDashR,
        0x0012 => MediaPhysicalType.DvdRam,
        0x0013 or 0x0014 or 0x0017 => MediaPhysicalType.DvdDashRw,
        0x0015 or 0x0016 => MediaPhysicalType.DvdDashRDualLayer,
        0x001A => MediaPhysicalType.DvdPlusRw,
        0x001B => MediaPhysicalType.DvdPlusR,
        0x002A => MediaPhysicalType.DvdPlusRwDualLayer,
        0x002B => MediaPhysicalType.DvdPlusRDualLayer,
        0x0040 => MediaPhysicalType.BdRom,
        0x0041 or 0x0042 => MediaPhysicalType.BdR,
        0x0043 => MediaPhysicalType.BdRe,
        0x0050 => MediaPhysicalType.HdDvdRom,
        0x0051 or 0x0058 => MediaPhysicalType.HdDvdR,
        0x0052 => MediaPhysicalType.HdDvdRam,
        _ => MediaPhysicalType.Unknown,
    };

    public static ProfileInfo LookupProfile(int code) =>
        Profiles.GetValueOrDefault(code) ??
        new ProfileInfo(code, $"Unknown profile {code:X4}h", ProfileCategory.Other, false);

    public static FeatureInfo LookupFeature(int code) =>
        Features.GetValueOrDefault(code) ??
        new FeatureInfo(code, $"Unknown feature {code:X4}h");

    public static readonly IReadOnlyDictionary<int, ProfileInfo> Profiles = new Dictionary<int, ProfileInfo>
    {
        [0x0000] = new(0x0000, "No current profile",         ProfileCategory.None,           false),
        [0x0001] = new(0x0001, "Non-removable disk",         ProfileCategory.Disk,           true),
        [0x0002] = new(0x0002, "Removable disk",             ProfileCategory.Disk,           true),
        [0x0003] = new(0x0003, "MO Erasable",                ProfileCategory.MagnetoOptical, true),
        [0x0004] = new(0x0004, "MO Write-Once",              ProfileCategory.MagnetoOptical, true),
        [0x0005] = new(0x0005, "AS-MO",                      ProfileCategory.MagnetoOptical, true),
        [0x0008] = new(0x0008, "CD-ROM",                     ProfileCategory.Cd,             false),
        [0x0009] = new(0x0009, "CD-R",                       ProfileCategory.Cd,             true),
        [0x000A] = new(0x000A, "CD-RW",                      ProfileCategory.Cd,             true),
        [0x0010] = new(0x0010, "DVD-ROM",                    ProfileCategory.Dvd,            false),
        [0x0011] = new(0x0011, "DVD-R Sequential",           ProfileCategory.Dvd,            true),
        [0x0012] = new(0x0012, "DVD-RAM",                    ProfileCategory.Dvd,            true),
        [0x0013] = new(0x0013, "DVD-RW Restricted Overwrite",ProfileCategory.Dvd,            true),
        [0x0014] = new(0x0014, "DVD-RW Sequential",          ProfileCategory.Dvd,            true),
        [0x0015] = new(0x0015, "DVD-R DL Sequential",        ProfileCategory.Dvd,            true),
        [0x0016] = new(0x0016, "DVD-R DL Layer Jump",        ProfileCategory.Dvd,            true),
        [0x0017] = new(0x0017, "DVD-RW DL",                  ProfileCategory.Dvd,            true),
        [0x0018] = new(0x0018, "DVD-Download",               ProfileCategory.Dvd,            false),
        [0x001A] = new(0x001A, "DVD+RW",                     ProfileCategory.Dvd,            true),
        [0x001B] = new(0x001B, "DVD+R",                      ProfileCategory.Dvd,            true),
        [0x0020] = new(0x0020, "DDCD-ROM",                   ProfileCategory.Cd,             false),
        [0x0021] = new(0x0021, "DDCD-R",                     ProfileCategory.Cd,             true),
        [0x0022] = new(0x0022, "DDCD-RW",                    ProfileCategory.Cd,             true),
        [0x002A] = new(0x002A, "DVD+RW DL",                  ProfileCategory.Dvd,            true),
        [0x002B] = new(0x002B, "DVD+R DL",                   ProfileCategory.Dvd,            true),
        [0x0040] = new(0x0040, "BD-ROM",                     ProfileCategory.BluRay,         false),
        [0x0041] = new(0x0041, "BD-R Sequential (SRM)",      ProfileCategory.BluRay,         true),
        [0x0042] = new(0x0042, "BD-R Random (RRM)",          ProfileCategory.BluRay,         true),
        [0x0043] = new(0x0043, "BD-RE",                      ProfileCategory.BluRay,         true),
        [0x0050] = new(0x0050, "HD DVD-ROM",                 ProfileCategory.HdDvd,          false),
        [0x0051] = new(0x0051, "HD DVD-R",                   ProfileCategory.HdDvd,          true),
        [0x0052] = new(0x0052, "HD DVD-RAM",                 ProfileCategory.HdDvd,          true),
        [0x0053] = new(0x0053, "HD DVD-RW",                  ProfileCategory.HdDvd,          true),
        [0x0058] = new(0x0058, "HD DVD-R DL",                ProfileCategory.HdDvd,          true),
        [0x005A] = new(0x005A, "HD DVD-RW DL",               ProfileCategory.HdDvd,          true),
    };

    public static readonly IReadOnlyDictionary<int, FeatureInfo> Features = new Dictionary<int, FeatureInfo>
    {
        [0x0000] = new(0x0000, "Profile List"),
        [0x0001] = new(0x0001, "Core"),
        [0x0002] = new(0x0002, "Morphing"),
        [0x0003] = new(0x0003, "Removable Medium"),
        [0x0004] = new(0x0004, "Write Protect"),
        [0x0010] = new(0x0010, "Random Readable"),
        [0x001D] = new(0x001D, "Multi-Read"),
        [0x001E] = new(0x001E, "CD Read"),
        [0x001F] = new(0x001F, "DVD Read"),
        [0x0020] = new(0x0020, "Random Writable"),
        [0x0021] = new(0x0021, "Incremental Streaming Writable"),
        [0x0022] = new(0x0022, "Sector Erasable"),
        [0x0023] = new(0x0023, "Formattable"),
        [0x0024] = new(0x0024, "Hardware Defect Management"),
        [0x0025] = new(0x0025, "Write Once"),
        [0x0026] = new(0x0026, "Restricted Overwrite"),
        [0x0027] = new(0x0027, "CD-RW CAV Write"),
        [0x0028] = new(0x0028, "MRW (Mt. Rainier)"),
        [0x0029] = new(0x0029, "Enhanced Defect Reporting"),
        [0x002A] = new(0x002A, "DVD+RW"),
        [0x002B] = new(0x002B, "DVD+R"),
        [0x002C] = new(0x002C, "Rigid Restricted Overwrite"),
        [0x002D] = new(0x002D, "CD Track at Once"),
        [0x002E] = new(0x002E, "CD Mastering / Session at Once"),
        [0x002F] = new(0x002F, "DVD-R/-RW Write"),
        [0x0030] = new(0x0030, "Double Density CD Read"),
        [0x0031] = new(0x0031, "Double Density CD-R Write"),
        [0x0032] = new(0x0032, "Double Density CD-RW Write"),
        [0x0033] = new(0x0033, "Layer Jump Recording"),
        [0x0037] = new(0x0037, "CD-RW Media Write Support"),
        [0x0038] = new(0x0038, "BD-R Pseudo-Overwrite (POW)"),
        [0x003A] = new(0x003A, "DVD+RW Dual Layer"),
        [0x003B] = new(0x003B, "DVD+R Dual Layer"),
        [0x0040] = new(0x0040, "BD Read"),
        [0x0041] = new(0x0041, "BD Write"),
        [0x0042] = new(0x0042, "Timely Safe Recording (TSR)"),
        [0x0050] = new(0x0050, "HD DVD Read"),
        [0x0051] = new(0x0051, "HD DVD Write"),
        [0x0052] = new(0x0052, "HD DVD-RW Fragment Recording"),
        [0x0080] = new(0x0080, "Hybrid Disc"),
        [0x0100] = new(0x0100, "Power Management"),
        [0x0101] = new(0x0101, "S.M.A.R.T."),
        [0x0102] = new(0x0102, "Embedded Changer"),
        [0x0103] = new(0x0103, "CD Audio Analog Play"),
        [0x0104] = new(0x0104, "Microcode Upgrade"),
        [0x0105] = new(0x0105, "Timeout"),
        [0x0106] = new(0x0106, "DVD CSS"),
        [0x0107] = new(0x0107, "Real-Time Streaming"),
        [0x0108] = new(0x0108, "Drive Serial Number"),
        [0x0109] = new(0x0109, "Media Serial Number"),
        [0x010A] = new(0x010A, "Disc Control Blocks"),
        [0x010B] = new(0x010B, "DVD CPRM"),
        [0x010C] = new(0x010C, "Firmware Information"),
        [0x010D] = new(0x010D, "AACS"),
        [0x010E] = new(0x010E, "DVD CSS Managed Recording"),
        [0x0110] = new(0x0110, "VCPS"),
        [0x0113] = new(0x0113, "SecurDisc"),
        [0x0142] = new(0x0142, "OSSC (Optical Security Subsystem Class)"),
    };
}
