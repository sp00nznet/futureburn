namespace Futureburn.Core.Imapi;

public sealed record OpticalDrive(
    string UniqueId,
    string VendorId,
    string ProductId,
    string Revision,
    IReadOnlyList<string> MountPoints,
    bool CanLoadMedia,
    IReadOnlyList<Mmc.ProfileInfo> SupportedProfiles,
    IReadOnlyList<Mmc.ProfileInfo> CurrentProfiles,
    IReadOnlyList<Mmc.FeatureInfo> SupportedFeaturePages,
    IReadOnlyList<Mmc.FeatureInfo> CurrentFeaturePages)
{
    public string? PrimaryMount => MountPoints.FirstOrDefault();

    public IReadOnlyList<Mmc.ProfileInfo> WritableProfiles =>
        SupportedProfiles.Where(p => p.Writable && p.Code != 0).ToArray();

    public IReadOnlyList<Mmc.ProfileInfo> ReadOnlyProfiles =>
        SupportedProfiles.Where(p => !p.Writable && p.Code != 0).ToArray();
}
