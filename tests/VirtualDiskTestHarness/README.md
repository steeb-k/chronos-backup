# Virtual Disk Test Harness

Quick test harness for `CreateVirtualDisk` / VHDX creation. Run without rebuilding the full app.

## How to run

```powershell
# From repo root - run as Administrator
dotnet run --project tests/VirtualDiskTestHarness

# Optional: specify output path
dotnet run --project tests/VirtualDiskTestHarness -- C:\Temp\test.vhdx
```

## What it does

1. **Test 1**: Calls `VirtualDiskService.CreateDynamicVhdxAsync` (same path as the app)
2. **Test 2**: Calls `CreateVirtualDisk` directly with parameter dump
3. **Test 3**: Retries with default block size (0) instead of 32 MB

Output shows exact error codes and parameters. Use results to isolate the failing parameter.
