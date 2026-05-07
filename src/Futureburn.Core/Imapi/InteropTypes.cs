using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace Futureburn.Core.Imapi;

// Typed [ComImport] declarations for the slice of IMAPI2 we need on the burn
// path. Why typed instead of dynamic: when we set `tao.Recorder = recorder`
// through `dynamic`, both ends are bare __ComObject and the marshaler has
// to resolve everything at runtime. In practice that resolution is fragile —
// the put_Recorder slot can fire without the drive context being established
// the way IMAPI's state machine expects, and PrepareMedia later fails with
// a SCSI mode-page error that doesn't really have anything to do with the
// disc. Typed interfaces remove the guesswork.
//
// Vtable rules: every method up to and including the last one we call must
// be declared in exact COM IDL order. Methods after our last-used one can
// be omitted. Inheritance from a base COM interface is "flattened" — base
// methods come first in our declaration, in IDL order.

[ComImport]
[Guid("27354133-7F64-5B0F-8F00-5D77AFBE261E")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
[SupportedOSPlatform("windows")]
public interface IDiscRecorder2
{
    void EjectMedia();
    void CloseTray();

    void AcquireExclusiveAccess(
        [In, MarshalAs(UnmanagedType.VariantBool)] bool force,
        [In, MarshalAs(UnmanagedType.BStr)] string clientName);

    void ReleaseExclusiveAccess();

    void DisableMcn();
    void EnableMcn();

    void InitializeDiscRecorder([In, MarshalAs(UnmanagedType.BStr)] string recorderUniqueId);

    // (Several property getters live below this in the vtable — VendorId, ProductId,
    // VolumePathNames, SupportedFeaturePages, SupportedProfiles, etc. We don't read
    // them through this typed interface; DriveEnumerator does that via dynamic.)
}

[ComImport]
[Guid("27354154-8F64-5B0F-8F00-5D77AFBE261E")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
[SupportedOSPlatform("windows")]
public interface IDiscFormat2TrackAtOnce
{
    // ---- Inherited from IDiscFormat2 (these come first in the vtable) ----
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool IsRecorderSupported([In, MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 recorder);

    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool IsCurrentMediaSupported([In, MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 recorder);

    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_MediaPhysicallyBlank();

    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_MediaHeuristicallyBlank();

    [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)]
    int[] get_SupportedMediaTypes();

    // ---- IDiscFormat2TrackAtOnce members ----
    void PrepareMedia();

    void AddAudioTrack([In, MarshalAs(UnmanagedType.Interface)] IStream data);

    void CancelAddTrack();
    void ReleaseMedia();

    void SetWriteSpeed(
        [In] int requestedSectorsPerSecond,
        [In, MarshalAs(UnmanagedType.VariantBool)] bool rotationTypeIsPureCAV);

    void put_Recorder([In, MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 value);

    [return: MarshalAs(UnmanagedType.Interface)]
    IDiscRecorder2 get_Recorder();

    void put_BufferUnderrunFreeDisabled([In, MarshalAs(UnmanagedType.VariantBool)] bool value);

    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_BufferUnderrunFreeDisabled();

    int get_NumberOfExistingTracks();
    int get_TotalSectorsOnMedia();
    int get_FreeSectorsOnMedia();
    int get_UsedSectorsOnMedia();

    void put_DoNotFinalizeMedia([In, MarshalAs(UnmanagedType.VariantBool)] bool value);

    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_DoNotFinalizeMedia();

    [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)]
    int[] get_ExpectedTableOfContents();

    int get_CurrentMediaType();

    void put_ClientName([In, MarshalAs(UnmanagedType.BStr)] string value);

    [return: MarshalAs(UnmanagedType.BStr)]
    string get_ClientName();

    int get_CurrentWriteSpeed();

    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_CurrentRotationTypeIsPureCAV();

    [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)]
    int[] get_SupportedWriteSpeeds();

    // (get_SupportedWriteSpeedDescriptors lives below this — we don't need it.)
}

// CoClass GUIDs (CLSIDs) used to instantiate the implementations.

[ComImport]
[Guid("2735412D-7F64-5B0F-8F00-5D77AFBE261E")]
public class MsftDiscRecorder2Class { }

[ComImport]
[Guid("27354129-7F64-5B0F-8F00-5D77AFBE261E")]
public class MsftDiscFormat2TrackAtOnceClass { }
