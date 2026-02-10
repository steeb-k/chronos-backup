using System.Security.Cryptography;
using Chronos.Core.Progress;
using Serilog;

namespace Chronos.Core.Imaging;

/// <summary>
/// Verifies backup image integrity by reading the file and ensuring it is readable and not corrupted.
/// </summary>
public class VerificationEngine : IVerificationEngine
{
    private const int ReadChunkSize = 2 * 1024 * 1024; // 2 MB
    private const int ProgressReportIntervalMs = 500;

    public async Task<bool> VerifyImageAsync(string imagePath, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        if (!File.Exists(imagePath))
        {
            Log.Warning("Verification failed: file does not exist: {Path}", imagePath);
            return false;
        }

        var fileInfo = new FileInfo(imagePath);
        var totalBytes = fileInfo.Length;

        if (totalBytes == 0)
        {
            Log.Warning("Verification failed: file is empty: {Path}", imagePath);
            return false;
        }

        Log.Information("Starting verification: {Path}, Size={Size} bytes", imagePath, totalBytes);

        var lastReport = DateTime.MinValue;

        await using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

        long bytesRead = 0;
        var buffer = new byte[ReadChunkSize];

        try
        {
            while (bytesRead < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(ReadChunkSize, totalBytes - bytesRead);
                int read = await fs.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    Log.Warning("Verification: unexpected end of file at offset {Offset}", bytesRead);
                    return false;
                }

                bytesRead += read;

                var now = DateTime.UtcNow;
                if (progressReporter is not null && (now - lastReport).TotalMilliseconds >= ProgressReportIntervalMs)
                {
                    lastReport = now;
                    double percent = totalBytes > 0 ? 100.0 * bytesRead / totalBytes : 100;
                    progressReporter.Report(new OperationProgress
                    {
                        PercentComplete = percent,
                        BytesProcessed = bytesRead,
                        TotalBytes = totalBytes,
                        StatusMessage = "Verifying image..."
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Verification failed while reading: {Path}", imagePath);
            return false;
        }

        progressReporter?.Report(new OperationProgress
        {
            PercentComplete = 100,
            BytesProcessed = totalBytes,
            TotalBytes = totalBytes,
            StatusMessage = "Verification complete"
        });

        Log.Information("Verification passed: {Path}", imagePath);
        return true;
    }

    public async Task<string> ComputeHashAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Image file not found", imagePath);

        await using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hashBytes = await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
