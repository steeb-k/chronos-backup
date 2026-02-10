using System.Runtime.InteropServices;
using Chronos.Core.VirtualDisk;
using Chronos.Native.VirtDisk;
using Microsoft.Win32.SafeHandles;

Console.WriteLine("=== Virtual Disk Creation Test Harness ===");
Console.WriteLine("Run as Administrator for disk operations.");
Console.WriteLine();

string path = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "chronos-test.vhdx");
long sizeBytes = 100 * 1024 * 1024; // 100 MB

Console.WriteLine($"Path: {path}");
Console.WriteLine($"Size: {sizeBytes} bytes ({sizeBytes / (1024 * 1024)} MB)");
Console.WriteLine();

// Test 1: Via VirtualDiskService (same code path as app)
Console.WriteLine("--- Test 1: VirtualDiskService.CreateDynamicVhdxAsync ---");
try
{
    var service = new VirtualDiskService();
    await service.CreateDynamicVhdxAsync(path, sizeBytes);
    Console.WriteLine("SUCCESS: VHDX created via VirtualDiskService");
    if (File.Exists(path))
    {
        File.Delete(path);
        Console.WriteLine("Cleaned up test file.");
    }
}
catch (Exception ex)
{
    int lastError = Marshal.GetLastWin32Error();
    Console.WriteLine($"FAILED: {ex.Message}");
    Console.WriteLine($"Last Win32 Error: {lastError} (0x{lastError:X})");
    Console.WriteLine($"Full exception: {ex}");
}

Console.WriteLine();

// Test 2: Direct P/Invoke with verbose parameter dump (helps isolate which param causes 87)
Console.WriteLine("--- Test 2: Direct CreateVirtualDisk with parameter dump ---");
path = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "chronos-test2.vhdx");
if (File.Exists(path)) File.Delete(path);

var storageType = new VirtualDiskInterop.VirtualStorageTypeStruct
{
    DeviceId = VirtualDiskInterop.VirtualStorageType.VHDX,
    VendorId = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B")
};

var parameters = new VirtualDiskInterop.CreateVirtualDiskParametersV2
{
    Version = VirtualDiskInterop.CreateVirtualDiskVersion.Version2,
    UniqueId = Guid.NewGuid(),
    MaximumSize = (ulong)sizeBytes,
    BlockSizeInBytes = 32 * 1024 * 1024,
    SectorSizeInBytes = 512,
    PhysicalSectorSizeInBytes = 512,
    ParentPath = IntPtr.Zero,
    SourcePath = IntPtr.Zero,
    OpenFlags = VirtualDiskInterop.OpenVirtualDiskFlags.None,
    ParentVirtualStorageType = default,
    SourceVirtualStorageType = default,
    ResiliencyGuid = Guid.Empty
};

Console.WriteLine("Parameters being passed:");
Console.WriteLine($"  Version: {parameters.Version}");
Console.WriteLine($"  MaximumSize: {parameters.MaximumSize}");
Console.WriteLine($"  BlockSizeInBytes: {parameters.BlockSizeInBytes}");
Console.WriteLine($"  SectorSizeInBytes: {parameters.SectorSizeInBytes}");
Console.WriteLine($"  PhysicalSectorSizeInBytes: {parameters.PhysicalSectorSizeInBytes}");
Console.WriteLine($"  VirtualDiskAccessMask: None (0)");

uint result = VirtualDiskInterop.CreateVirtualDisk(
    ref storageType,
    path,
    VirtualDiskInterop.VirtualDiskAccessMask.None,
    IntPtr.Zero,
    VirtualDiskInterop.CreateVirtualDiskFlags.None,
    0,
    ref parameters,
    IntPtr.Zero,
    out SafeFileHandle handle);

int err = Marshal.GetLastWin32Error();
Console.WriteLine();
Console.WriteLine($"CreateVirtualDisk result: {result} (0x{result:X})");
Console.WriteLine($"GetLastWin32Error: {err} (0x{err:X})");

if (result == 0)
{
    Console.WriteLine("SUCCESS");
    handle.Dispose();
    if (File.Exists(path)) File.Delete(path);
}
else
{
    Console.WriteLine($"FAILED - Error {result}");
    Console.WriteLine("Common causes of 87: access mask not 0 for V2, block size invalid, path issues, not admin");
}

Console.WriteLine();

// Test 3: Try default block size (0) instead of 32 MB - docs say 0 = default 2 MB
Console.WriteLine("--- Test 3: CreateVirtualDisk with default block size (0) ---");
path = args.Length > 0 ? Path.Combine(Path.GetDirectoryName(args[0])!, "chronos-test3.vhdx") : Path.Combine(Path.GetTempPath(), "chronos-test3.vhdx");
if (File.Exists(path)) File.Delete(path);

parameters.BlockSizeInBytes = 0; // CREATE_VIRTUAL_DISK_PARAMETERS_DEFAULT_BLOCK_SIZE
result = VirtualDiskInterop.CreateVirtualDisk(
    ref storageType,
    path,
    VirtualDiskInterop.VirtualDiskAccessMask.None,
    IntPtr.Zero,
    VirtualDiskInterop.CreateVirtualDiskFlags.None,
    0,
    ref parameters,
    IntPtr.Zero,
    out handle);

Console.WriteLine($"Result with BlockSizeInBytes=0: {result} (0x{result:X})");
if (result == 0)
{
    Console.WriteLine("SUCCESS - default block size works");
    handle.Dispose();
    if (File.Exists(path)) File.Delete(path);
}
