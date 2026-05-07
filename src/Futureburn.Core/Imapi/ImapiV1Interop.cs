using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Futureburn.Core.Imapi;

// Typed [ComImport] declarations for IMAPI v1 — the Windows XP-era CD writing API.
// Why bother in 2026: IMAPI v2 (the modern API) doesn't work on every drive. Some
// older USB writers (the LG GE20LU10, FE06 firmware, for example) reject v2's
// PrepareMedia with "mode page not present" while v1's RecordDisc path may still
// succeed on the same hardware.
//
// IMAPI v1 interfaces are vtable-only IUnknown (NOT IDispatch). PowerShell can't
// talk to them at all. We must declare the typed interfaces ourselves and call
// them through C# — there's no dynamic fallback.
//
// All GUIDs come from imapi.idl in the Windows SDK.

[ComImport]
[Guid("520CCA63-51A5-11D3-9144-00104BA11C5E")]
[SupportedOSPlatform("windows")]
public class MsDiscMasterObjClass { }

[ComImport]
[Guid("520CCA62-51A5-11D3-9144-00104BA11C5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
public interface IDiscMaster
{
    void Open();

    void EnumDiscMasterFormats(out IEnumDiscMasterFormats ppEnum);

    void GetActiveDiscMasterFormat(out Guid pFormatId);

    void SetActiveDiscMasterFormat(
        [In] ref Guid pFormatId,
        [Out, MarshalAs(UnmanagedType.IUnknown)] out object ppObject);

    void EnumDiscRecorders(out IEnumDiscRecorders ppEnum);

    void GetActiveDiscRecorder(out IDiscRecorder ppRecorder);

    void SetActiveDiscRecorder([In] IDiscRecorder pRecorder);

    void ClearFormatContent();

    void ProgressAdvise(
        [In] IDiscMasterProgressEvents pEvents,
        out IntPtr pdwCookie);

    void ProgressUnadvise([In] IntPtr dwCookie);

    void RecordDisc(
        [In, MarshalAs(UnmanagedType.Bool)] bool bSimulate,
        [In, MarshalAs(UnmanagedType.Bool)] bool bEjectAfterBurn);

    void Close();
}

[ComImport]
[Guid("9B1921E0-54AC-11D3-9144-00104BA11C5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
public interface IEnumDiscMasterFormats
{
    [PreserveSig]
    int Next(
        [In] uint cFormats,
        [Out, MarshalAs(UnmanagedType.LPArray)] Guid[] lpiidFormatID,
        out uint pcFetched);
    void Skip([In] uint cFormats);
    void Reset();
    void Clone(out IEnumDiscMasterFormats ppEnum);
}

[ComImport]
[Guid("9B1921E1-54AC-11D3-9144-00104BA11C5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
public interface IEnumDiscRecorders
{
    [PreserveSig]
    int Next(
        [In] uint cRecorders,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IDiscRecorder[] ppRecorder,
        out uint pcFetched);
    void Skip([In] uint cRecorders);
    void Reset();
    void Clone(out IEnumDiscRecorders ppEnum);
}

[ComImport]
[Guid("85AC9776-CA88-4CF2-894E-09598C078A41")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
public interface IDiscRecorder
{
    // We don't need Init() but it's first in the vtable so we declare it as a stub.
    void Init([In] IntPtr pbtId, [In] int nDeviceId, [In] int nBusId, [In] int nLunId);

    void GetRecorderGUID(
        [Out] IntPtr pbtUniqueIdGUID,
        [In] int dwBufferSize,
        out int pdwReturnSizeNeeded);

    void GetRecorderType(out int fTypeCode);

    void GetDisplayNames(
        [In, Out, MarshalAs(UnmanagedType.BStr)] ref string pbstrVendorID,
        [In, Out, MarshalAs(UnmanagedType.BStr)] ref string pbstrProductID,
        [In, Out, MarshalAs(UnmanagedType.BStr)] ref string pbstrRevision);

    void GetBasePnPID([Out, MarshalAs(UnmanagedType.BStr)] out string pbstrBasePnPID);

    void GetPath([Out, MarshalAs(UnmanagedType.BStr)] out string pbstrPath);

    // (More methods follow in the vtable — GetRecorderProperties, OpenExclusive,
    //  QueryMediaType, etc. We don't need them for our minimal path.)
}

[ComImport]
[Guid("EC9E51C1-4E5D-11D3-9144-00104BA11C5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
public interface IDiscMasterProgressEvents
{
    [PreserveSig] int QueryCancel([Out, MarshalAs(UnmanagedType.Bool)] out bool pbCancel);
    [PreserveSig] int NotifyPnPActivity();
    [PreserveSig] int NotifyAddProgress([In] int nCompletedSteps, [In] int nTotalSteps);
    [PreserveSig] int NotifyBlockProgress([In] int nCompleted, [In] int nTotal);
    [PreserveSig] int NotifyTrackProgress([In] int nCurrentTrack, [In] int nTotalTracks);
    [PreserveSig] int NotifyPreparingBurn([In] int nEstimatedSeconds);
    [PreserveSig] int NotifyClosingDisc([In] int nEstimatedSeconds);
    [PreserveSig] int NotifyBurnComplete([In, MarshalAs(UnmanagedType.Error)] int status);
    [PreserveSig] int NotifyEraseComplete([In, MarshalAs(UnmanagedType.Error)] int status);
}

[ComImport]
[Guid("E3BC42CD-4E5C-11D3-9144-00104BA11C5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
public interface IRedbookDiscMaster
{
    void GetTotalAudioTracks(out int pnTracks);
    void GetTotalAudioBlocks(out int pnBlocks);
    void GetUsedAudioBlocks(out int pnBlocks);
    void GetAvailableAudioTrackBlocks(out int pnBlocks);
    void GetAudioBlockSize(out int pnBlockBytes);
    void CreateAudioTrack([In] int nBlocks);
    void AddAudioTrackBlocks(
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] pby,
        [In] int cb);
    void CloseAudioTrack();
}

// Format identifier GUIDs for SetActiveDiscMasterFormat.
public static class ImapiV1Formats
{
    // IID_IRedbookDiscMaster doubles as the format ID for selecting "audio CD" mode.
    public static readonly Guid Redbook = new("E3BC42CD-4E5C-11D3-9144-00104BA11C5E");

    // IID_IJolietDiscMaster — selects "data CD (Joliet)" mode.
    public static readonly Guid Joliet  = new("E3BC42CE-4E5C-11D3-9144-00104BA11C5E");
}
