using Chronos.Core.Disk;

Console.WriteLine("=== Allocated Ranges Test Harness ===");
Console.WriteLine("Run as Administrator. Usage: AllocatedRangesTestHarness [volume] [size]");
Console.WriteLine("  volume: e.g. \\\\.\\E: (default: \\\\.\\E:)");
Console.WriteLine("  size: optional partition size in bytes");
Console.WriteLine();

string volumePath = args.Length > 0 ? args[0] : @"\\.\E:";
if (!volumePath.StartsWith(@"\\.\"))
    volumePath = @"\\.\" + volumePath.TrimStart('\\');
if (!volumePath.EndsWith(":"))
    volumePath += ":";

ulong partitionSize = 0;
if (args.Length >= 2 && ulong.TryParse(args[1], out var size))
    partitionSize = size;
if (partitionSize == 0)
{
    var driveRoot = volumePath.Length >= 4 ? volumePath.Substring(4) + "\\" : volumePath + "\\";
    if (Chronos.Native.Win32.VolumeApi.GetDiskFreeSpace(driveRoot, out uint spc, out uint bps, out _, out uint totalClusters))
        partitionSize = (ulong)totalClusters * spc * bps;
}
if (partitionSize == 0)
    partitionSize = 100 * 1024 * 1024 * 1024UL;

Console.WriteLine($"Volume: {volumePath}, Size: {partitionSize} bytes ({partitionSize / (1024 * 1024 * 1024)} GB)");
Console.WriteLine();

var provider = new AllocatedRangesProvider();
var ranges = await provider.GetAllocatedRangesAsync(volumePath, partitionSize);
if (ranges is not null)
{
    long total = 0;
    foreach (var r in ranges) total += r.Length;
    Console.WriteLine($"SUCCESS: {ranges.Count} ranges, {total} bytes ({100.0 * total / partitionSize:F1}% allocated)");
}
else
    Console.WriteLine("FAILED: Provider returned null (check logs, run as Administrator)");

Console.WriteLine("\nDone.");
