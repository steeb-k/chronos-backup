using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Chronos.Core.Models;
using Chronos.Core.Progress;
using Chronos.Core.VirtualDisk;
using Serilog;

namespace Chronos.Core.Imaging;

/// <summary>
/// Checks filesystem integrity of a VHDX image by mounting it read-only and running
/// chkdsk /scan. Falls back to reading the NTFS boot sector when chkdsk is unavailable
/// (e.g., WinPE minimal environments).
/// </summary>
public class FilesystemChecker : IFilesystemChecker
{
    private const int ChkdskTimeoutSeconds = 120;

    private readonly IVirtualDiskService _virtualDiskService;

    public FilesystemChecker(IVirtualDiskService virtualDiskService)
    {
        _virtualDiskService = virtualDiskService ?? throw new ArgumentNullException(nameof(virtualDiskService));
    }

    public async Task<FilesystemCheckResult> CheckAsync(
        string vhdxPath,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vhdxPath);

        // Only VHDX/VHD files can be mounted for checking
        string ext = Path.GetExtension(vhdxPath).ToLowerInvariant();
        if (ext is not (".vhdx" or ".vhd"))
        {
            Log.Debug("FilesystemChecker: skipping non-VHDX file {Path}", vhdxPath);
            return new FilesystemCheckResult
            {
                IsHealthy = true,
                Summary = "Filesystem check skipped (not a VHDX image)"
            };
        }

        if (!File.Exists(vhdxPath))
        {
            Log.Warning("FilesystemChecker: source image not found at {Path}", vhdxPath);
            return new FilesystemCheckResult
            {
                IsHealthy = false,
                Summary = "Could not check: source image file not found"
            };
        }

        Log.Information("FilesystemChecker: starting check on {Path}", vhdxPath);
        progressReporter?.Report(new OperationProgress
        {
            StatusMessage = "Mounting image for filesystem check...",
            PercentComplete = 100
        });

        char driveLetter;
        try
        {
            driveLetter = await _virtualDiskService.MountToDriveLetterAsync(vhdxPath, readOnly: true)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("no drive letter", StringComparison.OrdinalIgnoreCase))
        {
            // VHDX attached but Windows didn't assign a drive letter — likely an EFI-only
            // or recovery-only disk with no data partition.
            Log.Information("FilesystemChecker: no drive letter assigned for {Path} — no data partition to check", vhdxPath);
            return new FilesystemCheckResult
            {
                IsHealthy = true,
                Summary = "No data partition found to check"
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FilesystemChecker: failed to mount {Path}", vhdxPath);
            return new FilesystemCheckResult
            {
                IsHealthy = false,
                Summary = $"Could not mount image for check: {ex.Message}"
            };
        }

        try
        {
            return await RunCheckAsync(vhdxPath, driveLetter, progressReporter, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _virtualDiskService.DismountAsync(vhdxPath).ConfigureAwait(false);
                Log.Debug("FilesystemChecker: dismounted {Path}", vhdxPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FilesystemChecker: failed to dismount {Path}", vhdxPath);
            }
        }
    }

    private async Task<FilesystemCheckResult> RunCheckAsync(
        string vhdxPath,
        char driveLetter,
        IProgressReporter? progressReporter,
        CancellationToken cancellationToken)
    {
        string chkdskPath = Path.Combine(Environment.SystemDirectory, "chkdsk.exe");

        if (File.Exists(chkdskPath))
        {
            return await RunChkdskAsync(driveLetter, chkdskPath, progressReporter, cancellationToken)
                .ConfigureAwait(false);
        }

        // chkdsk not available — WinPE minimal image
        Log.Information("FilesystemChecker: chkdsk.exe not found, using boot-sector fallback for drive {Letter}:", driveLetter);
        progressReporter?.Report(new OperationProgress
        {
            StatusMessage = "chkdsk unavailable — checking boot sector...",
            PercentComplete = 100
        });
        return RunBootSectorCheck(driveLetter);
    }

    private async Task<FilesystemCheckResult> RunChkdskAsync(
        char driveLetter,
        string chkdskPath,
        IProgressReporter? progressReporter,
        CancellationToken cancellationToken)
    {
        progressReporter?.Report(new OperationProgress
        {
            StatusMessage = $"Running filesystem check on {driveLetter}:...",
            PercentComplete = 100
        });

        var psi = new ProcessStartInfo
        {
            FileName = chkdskPath,
            Arguments = $"{driveLetter}: /scan /perf",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(ChkdskTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                Log.Warning("FilesystemChecker: failed to start chkdsk.exe");
                return new FilesystemCheckResult
                {
                    IsHealthy = false,
                    Summary = "Could not start chkdsk.exe"
                };
            }

            var outputTask = proc.StandardOutput.ReadToEndAsync(linkedCts.Token);
            await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

            string output;
            try { output = await outputTask.ConfigureAwait(false); }
            catch { output = string.Empty; }

            int exitCode = proc.ExitCode;
            Log.Information("FilesystemChecker: chkdsk exit code {Code} for drive {Letter}:", exitCode, driveLetter);
            Log.Debug("FilesystemChecker: chkdsk output: {Output}", output.Trim());

            return exitCode switch
            {
                0 => new FilesystemCheckResult
                {
                    IsHealthy = true,
                    Summary = "No errors found",
                    ChkdskExitCode = 0
                },
                2 => new FilesystemCheckResult
                {
                    IsHealthy = false,
                    Summary = "Filesystem has errors that may need repair",
                    ChkdskExitCode = 2
                },
                _ => new FilesystemCheckResult
                {
                    IsHealthy = false,
                    Summary = $"Filesystem check could not complete (chkdsk exit {exitCode})",
                    ChkdskExitCode = exitCode
                }
            };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Log.Warning("FilesystemChecker: chkdsk timed out after {Seconds}s", ChkdskTimeoutSeconds);
            return new FilesystemCheckResult
            {
                IsHealthy = false,
                Summary = $"Filesystem check timed out after {ChkdskTimeoutSeconds}s",
                ChkdskExitCode = null
            };
        }
    }

    private static FilesystemCheckResult RunBootSectorCheck(char driveLetter)
    {
        // Read the first 512 bytes of the volume and check for known filesystem signatures.
        // This is a lightweight fallback for WinPE where chkdsk.exe is absent.
        try
        {
            using var stream = new FileStream(
                $"\\\\.\\{driveLetter}:",
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 512,
                useAsync: false);

            var sector = new byte[512];
            int read = stream.Read(sector, 0, 512);
            if (read < 512)
            {
                return new FilesystemCheckResult
                {
                    IsHealthy = false,
                    Summary = "Boot sector unreadable",
                    UsedFallback = true
                };
            }

            // Check for known OEM IDs at bytes 3–10
            string oemId = System.Text.Encoding.ASCII.GetString(sector, 3, 8);
            string fsType = oemId.TrimEnd() switch
            {
                "NTFS"    => "NTFS",
                "FAT32"   => "FAT32",
                "EXFAT"   => "exFAT",
                "ReFS"    => "ReFS",
                _         => string.Empty
            };

            // Validate the standard boot signature at bytes 510–511
            bool hasBpbSignature = sector[510] == 0x55 && sector[511] == 0xAA;

            if (!string.IsNullOrEmpty(fsType) && hasBpbSignature)
            {
                Log.Information("FilesystemChecker: boot sector OK — {FS} signature found on {Letter}:", fsType, driveLetter);
                return new FilesystemCheckResult
                {
                    IsHealthy = true,
                    Summary = $"{fsType} boot sector OK",
                    UsedFallback = true
                };
            }

            if (hasBpbSignature)
            {
                // Signature present but OEM ID is not a known string — still bootable
                Log.Information("FilesystemChecker: boot sector has valid signature on {Letter}: (OEM: '{OEM}')", driveLetter, oemId.Trim());
                return new FilesystemCheckResult
                {
                    IsHealthy = true,
                    Summary = "Boot sector signature present",
                    UsedFallback = true
                };
            }

            Log.Warning("FilesystemChecker: no valid boot signature on {Letter}: (OEM: '{OEM}')", driveLetter, oemId.Trim());
            return new FilesystemCheckResult
            {
                IsHealthy = false,
                Summary = "No valid boot sector signature found",
                UsedFallback = true
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FilesystemChecker: boot sector check failed for drive {Letter}:", driveLetter);
            return new FilesystemCheckResult
            {
                IsHealthy = false,
                Summary = $"Boot sector check failed: {ex.Message}",
                UsedFallback = true
            };
        }
    }
}
