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

    [DllImport(VssApiDll, EntryPoint = "CreateVssBackupComponentsInternal", ExactSpelling = true, PreserveSig = false)]
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
[Guid("665c1d5f-c218-414d-a05d-7fef5f9d5c86")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVssBackupComponents
{
    // Vtable slots must match the real COM interface exactly.
    // Unused methods are declared as stubs to keep offsets correct.

    // Slot 0
    [PreserveSig] int GetWriterComponentsCount(out uint pcComponents);
    // Slot 1
    [PreserveSig] int GetWriterComponents(uint iWriter, out IntPtr ppWriter);
    // Slot 2
    [PreserveSig] int InitializeForBackup([MarshalAs(UnmanagedType.BStr)] string? bstrXml);
    // Slot 3
    [PreserveSig] int SetBackupState(
        [MarshalAs(UnmanagedType.Bool)] bool bSelectComponents,
        [MarshalAs(UnmanagedType.Bool)] bool bBackupBootableSystemState,
        int backupType,
        [MarshalAs(UnmanagedType.Bool)] bool bPartialFileSupport);
    // Slot 4
    [PreserveSig] int InitializeForRestore([MarshalAs(UnmanagedType.BStr)] string bstrXml);
    // Slot 5
    [PreserveSig] int SetRestoreState(int restoreType);
    // Slot 6
    [PreserveSig] int GatherWriterMetadata(out IVssAsync ppAsync);
    // Slot 7
    [PreserveSig] int GetWriterMetadataCount(out uint pcWriters);
    // Slot 8
    [PreserveSig] int GetWriterMetadata(uint iWriter, out Guid pidInstance, out IntPtr ppMetadata);
    // Slot 9
    [PreserveSig] int FreeWriterMetadata();
    // Slot 10
    [PreserveSig] int AddComponent(Guid instanceId, Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string? wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName);
    // Slot 11
    [PreserveSig] int PrepareForBackup(out IVssAsync ppAsync);
    // Slot 12
    [PreserveSig] int AbortBackup();
    // Slot 13
    [PreserveSig] int GatherWriterStatus(out IVssAsync ppAsync);
    // Slot 14
    [PreserveSig] int GetWriterStatusCount(out uint pcWriters);
    // Slot 15
    [PreserveSig] int FreeWriterStatus();
    // Slot 16
    [PreserveSig] int GetWriterStatus(uint iWriter, out Guid pidInstance, out Guid pidWriter, [MarshalAs(UnmanagedType.BStr)] out string pbstrWriter, out int pnStatus, out int phResultFailure);
    // Slot 17
    [PreserveSig] int SetBackupSucceeded(Guid instanceId, Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string? wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.Bool)] bool bSucceeded);
    // Slot 18
    [PreserveSig] int SetBackupOptions(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string? wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.LPWStr)] string wszBackupOptions);
    // Slot 19
    [PreserveSig] int SetSelectedForRestore(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string? wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.Bool)] bool bSelectedForRestore);
    // Slot 20
    [PreserveSig] int SetRestoreOptions(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string? wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.LPWStr)] string wszRestoreOptions);
    // Slot 21
    [PreserveSig] int SetAdditionalRestores(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string? wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.Bool)] bool bAdditionalRestores);
    // Slot 22
    [PreserveSig] int SetPreviousBackupStamp(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string? wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.LPWStr)] string wszPreviousBackupStamp);
    // Slot 23
    [PreserveSig] int SaveAsXML([MarshalAs(UnmanagedType.BStr)] out string pbstrXml);
    // Slot 24
    [PreserveSig] int BackupComplete(out IVssAsync ppAsync);
    // Slot 25
    [PreserveSig] int AddAlternativeLocationMapping(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string? wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.LPWStr)] string wszPath, [MarshalAs(UnmanagedType.LPWStr)] string wszFilespec, [MarshalAs(UnmanagedType.Bool)] bool bRecursive, [MarshalAs(UnmanagedType.LPWStr)] string wszDestination);
    // Slot 26
    [PreserveSig] int AddRestoreSubcomponent(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string? wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.LPWStr)] string wszSubComponentLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszSubComponentName, [MarshalAs(UnmanagedType.Bool)] bool bRepair);
    // Slot 27
    [PreserveSig] int SetFileRestoreStatus(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string? wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, int status);
    // Slot 28
    [PreserveSig] int AddNewTarget(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string? wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, [MarshalAs(UnmanagedType.LPWStr)] string wszPath, [MarshalAs(UnmanagedType.LPWStr)] string wszFileName, [MarshalAs(UnmanagedType.Bool)] bool bRecursive, [MarshalAs(UnmanagedType.LPWStr)] string wszAlternatePath);
    // Slot 29
    [PreserveSig] int SetRangesFilePath(Guid writerId, int ct, [MarshalAs(UnmanagedType.LPWStr)] string? wszLogicalPath, [MarshalAs(UnmanagedType.LPWStr)] string wszComponentName, uint iPartialFile, [MarshalAs(UnmanagedType.LPWStr)] string wszRangesFile);
    // Slot 30
    [PreserveSig] int PreRestore(out IVssAsync ppAsync);
    // Slot 31
    [PreserveSig] int PostRestore(out IVssAsync ppAsync);
    // Slot 32
    [PreserveSig] int SetContext(int lContext);
    // Slot 33
    [PreserveSig] int StartSnapshotSet(out Guid pSnapshotSetId);
    // Slot 34
    [PreserveSig] int AddToSnapshotSet(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszVolumeName,
        Guid ProviderId,
        out Guid pSnapshotId);
    // Slot 35
    [PreserveSig] int DoSnapshotSet(out IVssAsync ppAsync);
    // Slot 36
    [PreserveSig] int DeleteSnapshots(
        Guid SourceObjectId,
        int eSourceObjectType,
        [MarshalAs(UnmanagedType.Bool)] bool bForceDelete,
        out int plDeletedSnapshots,
        out Guid pNonDeletedSnapshotId);
    // Slot 37
    [PreserveSig] int ImportSnapshots(out IVssAsync ppAsync);
    // Slot 38
    [PreserveSig] int BreakSnapshotSet(Guid snapshotSetId);
    // Slot 39
    [PreserveSig] int GetSnapshotProperties(Guid SnapshotId, out VSS_SNAPSHOT_PROP pProp);
    // Slot 40
    [PreserveSig] int Query(Guid QueriedObjectId, int eQueriedObjectType, int eReturnedObjectsType, out IntPtr ppEnum);
    // Slot 41
    [PreserveSig] int IsVolumeSupported(Guid ProviderId, [MarshalAs(UnmanagedType.LPWStr)] string pwszVolumeName, [MarshalAs(UnmanagedType.Bool)] out bool pbSupportedByThisProvider);
    // Slot 42
    [PreserveSig] int DisableWriterClasses([MarshalAs(UnmanagedType.LPArray)] Guid[] rgWriterClassId, uint cClassId);
    // Slot 43
    [PreserveSig] int EnableWriterClasses([MarshalAs(UnmanagedType.LPArray)] Guid[] rgWriterClassId, uint cClassId);
    // Slot 44
    [PreserveSig] int DisableWriterInstances([MarshalAs(UnmanagedType.LPArray)] Guid[] rgWriterInstanceId, uint cInstanceId);
    // Slot 45
    [PreserveSig] int ExposeSnapshot(Guid SnapshotId, [MarshalAs(UnmanagedType.LPWStr)] string? wszPathFromRoot, int lAttributes, [MarshalAs(UnmanagedType.LPWStr)] string? wszExpose, [MarshalAs(UnmanagedType.LPWStr)] out string pwszExposed);
    // Slot 46
    [PreserveSig] int RevertToSnapshot(Guid SnapshotId, [MarshalAs(UnmanagedType.Bool)] bool bForceDismount);
    // Slot 47
    [PreserveSig] int QueryRevertStatus([MarshalAs(UnmanagedType.LPWStr)] string pwszVolume, out IVssAsync ppAsync);
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
