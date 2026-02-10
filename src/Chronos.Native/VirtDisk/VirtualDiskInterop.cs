using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Chronos.Native.VirtDisk;

/// <summary>
/// P/Invoke declarations for Windows Virtual Disk API (virtdisk.dll)
/// </summary>
public static class VirtualDiskInterop
{
    private const string VirtDiskDll = "virtdisk.dll";

    [Flags]
    public enum VirtualDiskAccessMask : uint
    {
        None = 0x00000000,
        AttachReadOnly = 0x00010000,
        AttachReadWrite = 0x00020000,
        Detach = 0x00040000,
        GetInfo = 0x00080000,
        Create = 0x00100000,
        MetaOps = 0x00200000,
        Read = 0x000D0000,
        All = 0x003F0000,
        Writable = 0x00320000
    }

    [Flags]
    public enum OpenVirtualDiskFlags : uint
    {
        None = 0x00000000,
        NoParents = 0x00000001,
        BlankFile = 0x00000002,
        BootDrive = 0x00000004,
        CachedIO = 0x00000008,
        CustomDiffChain = 0x00000010,
        ParentCachedIO = 0x00000020,
        SupportCompressedVolumes = 0x00000040,
        SupportSparseVolumes = 0x00000080,
    }

    [Flags]
    public enum CreateVirtualDiskFlags : uint
    {
        None = 0x00000000,
        FullPhysicalAllocation = 0x00000001,
        PreventWritesToSourceDisk = 0x00000002,
        DoNotCopyMetadataFromParent = 0x00000004,
        CreateBackingStorage = 0x00000008,
        UseChangeTrackingSourceLimit = 0x00000010,
        PreserveParentChangeTrackingState = 0x00000020,
        VhdSetUseOriginalBackingStorage = 0x00000040,
        SparseFile = 0x00000080,
        PmemCompatible = 0x00000100,
        SupportCompressedVolumes = 0x00000200,
    }

    [Flags]
    public enum AttachVirtualDiskFlags : uint
    {
        None = 0x00000000,
        ReadOnly = 0x00000001,
        NoDriveLetter = 0x00000002,
        PermanentLifetime = 0x00000004,
        NoLocalHost = 0x00000008,
        NoSecurityDescriptor = 0x00000010,
        BypassDefaultEncryptionPolicy = 0x00000020,
        NonPnp = 0x00000040,
        RestrictedRange = 0x00000080,
        SinglePartition = 0x00000100,
        RegisterVolume = 0x00000200,
    }

    public enum VirtualStorageType : uint
    {
        Unknown = 0,
        ISO = 1,
        VHD = 2,
        VHDX = 3,
        VHDSet = 4,
    }

    public enum CreateVirtualDiskVersion : uint
    {
        Unspecified = 0,
        Version1 = 1,
        Version2 = 2,
    }

    public enum OpenVirtualDiskVersion : uint
    {
        Unspecified = 0,
        Version1 = 1,
        Version2 = 2,
        Version3 = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VirtualStorageTypeStruct
    {
        public VirtualStorageType DeviceId;
        public Guid VendorId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OpenVirtualDiskParametersV1
    {
        public OpenVirtualDiskVersion Version;
        public uint RWDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OpenVirtualDiskParametersV2
    {
        public OpenVirtualDiskVersion Version;
        [MarshalAs(UnmanagedType.Bool)]
        public bool GetInfoOnly;
        [MarshalAs(UnmanagedType.Bool)]
        public bool ReadOnly;
        public Guid ResiliencyGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CreateVirtualDiskParametersV2
    {
        public CreateVirtualDiskVersion Version;
        public Guid UniqueId;
        public ulong MaximumSize;
        public uint BlockSizeInBytes;
        public uint SectorSizeInBytes;
        public uint PhysicalSectorSizeInBytes;
        public IntPtr ParentPath;
        public IntPtr SourcePath;
        public OpenVirtualDiskFlags OpenFlags;
        public VirtualStorageTypeStruct ParentVirtualStorageType;
        public VirtualStorageTypeStruct SourceVirtualStorageType;
        public Guid ResiliencyGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AttachVirtualDiskParametersV1
    {
        public uint Version;
        public uint Reserved;
    }

    [DllImport(VirtDiskDll, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint OpenVirtualDisk(
        ref VirtualStorageTypeStruct VirtualStorageType,
        string Path,
        VirtualDiskAccessMask VirtualDiskAccessMask,
        OpenVirtualDiskFlags Flags,
        ref OpenVirtualDiskParametersV2 Parameters,
        out SafeFileHandle Handle);

    [DllImport(VirtDiskDll, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint CreateVirtualDisk(
        ref VirtualStorageTypeStruct VirtualStorageType,
        string Path,
        VirtualDiskAccessMask VirtualDiskAccessMask,
        IntPtr SecurityDescriptor,
        CreateVirtualDiskFlags Flags,
        uint ProviderSpecificFlags,
        ref CreateVirtualDiskParametersV2 Parameters,
        IntPtr Overlapped,
        out SafeFileHandle Handle);

    [DllImport(VirtDiskDll, SetLastError = true)]
    public static extern uint AttachVirtualDisk(
        SafeFileHandle VirtualDiskHandle,
        IntPtr SecurityDescriptor,
        AttachVirtualDiskFlags Flags,
        uint ProviderSpecificFlags,
        ref AttachVirtualDiskParametersV1 Parameters,
        IntPtr Overlapped);

    [DllImport(VirtDiskDll, SetLastError = true)]
    public static extern uint DetachVirtualDisk(
        SafeFileHandle VirtualDiskHandle,
        uint Flags,
        uint ProviderSpecificFlags);

    [DllImport(VirtDiskDll, SetLastError = true)]
    public static extern uint GetVirtualDiskPhysicalPath(
        SafeFileHandle VirtualDiskHandle,
        ref uint DiskPathSizeInBytes,
        IntPtr DiskPath);
}
