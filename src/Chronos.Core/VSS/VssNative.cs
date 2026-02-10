using System.Runtime.InteropServices;

namespace Chronos.Core.VSS;

[StructLayout(LayoutKind.Sequential)]
internal struct VSS_SNAPSHOT_PROP
{
    public Guid m_SnapshotId;
    public Guid m_SnapshotSetId;
    public int m_lSnapshotsCount;
    public IntPtr m_pwszSnapshotDeviceObject;
    public IntPtr m_pwszOriginalVolumeName;
    public IntPtr m_pwszOriginatingMachine;
    public IntPtr m_pwszServiceMachine;
    public IntPtr m_pwszExposedName;
    public IntPtr m_pwszExposedPath;
    public Guid m_ProviderId;
    public int m_lSnapshotAttributes;
    public long m_tsCreationTimestamp;
    public int m_eStatus;
}

/// <summary>
/// Pure P/Invoke bindings for Windows VSS API (VssApi.dll).
/// No C++/CLI or third-party native dependencies — works on x86, x64, and ARM64.
/// </summary>
internal static class VssNative
{
    private const string VssApiDll = "VssApi.dll";

    public const int VSS_CTX_BACKUP = 0;
    public const int VSS_BT_FULL = 1;
    public const int VSS_OBJECT_SNAPSHOT_SET = 2;
    public const uint INFINITE = 0xFFFFFFFF;

    [DllImport(VssApiDll, ExactSpelling = true, PreserveSig = false)]
    public static extern void CreateVssBackupComponents(
        [MarshalAs(UnmanagedType.Interface)] out IVssBackupComponents ppBackup);

    [DllImport(VssApiDll, ExactSpelling = true)]
    public static extern void VssFreeSnapshotProperties(ref VSS_SNAPSHOT_PROP pProp);

    public static string? GetSnapshotDeviceObject(ref VSS_SNAPSHOT_PROP prop)
    {
        if (prop.m_pwszSnapshotDeviceObject == IntPtr.Zero)
            return null;
        return Marshal.PtrToStringUni(prop.m_pwszSnapshotDeviceObject);
    }
}

[ComImport]
[Guid("665c1d5e-c218-414d-a302-8b5adce4bfb4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVssBackupComponents
{
    [PreserveSig]
    int InitializeForBackup([MarshalAs(UnmanagedType.BStr)] string? bstrXml);

    [PreserveSig]
    int SetContext(int lContext);

    [PreserveSig]
    int GatherWriterMetadata(out IVssAsync ppAsync);

    [PreserveSig]
    int StartSnapshotSet(out Guid pSnapshotSetId);

    [PreserveSig]
    int AddToSnapshotSet(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszVolumeName,
        Guid ProviderId,
        out Guid pSnapshotId);

    [PreserveSig]
    int SetBackupState(
        [MarshalAs(UnmanagedType.Bool)] bool bSelectComponents,
        [MarshalAs(UnmanagedType.Bool)] bool bBackupBootableSystemState,
        int backupType,
        [MarshalAs(UnmanagedType.Bool)] bool bPartialFileSupport);

    [PreserveSig]
    int PrepareForBackup(out IVssAsync ppAsync);

    [PreserveSig]
    int DoSnapshotSet(out IVssAsync ppAsync);

    [PreserveSig]
    int GetSnapshotProperties(Guid SnapshotId, out VSS_SNAPSHOT_PROP pProp);

    [PreserveSig]
    int DeleteSnapshots(
        Guid SourceObjectId,
        int eSourceObjectType,
        [MarshalAs(UnmanagedType.Bool)] bool bForceDelete,
        out int plDeletedSnapshots,
        out Guid pNonDeletedSnapshotId);
}

[ComImport]
[Guid("507c37b4-cf5b-4e95-b0af-14eb9767467e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVssAsync
{
    [PreserveSig]
    int Cancel();

    [PreserveSig]
    int Wait(uint dwMilliseconds);

    [PreserveSig]
    int QueryStatus(out int pHrResult, out int pReserved);
}
