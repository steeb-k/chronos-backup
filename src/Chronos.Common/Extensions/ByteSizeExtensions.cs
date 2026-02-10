namespace Chronos.Common.Extensions;

/// <summary>
/// Extension methods for byte size formatting.
/// </summary>
public static class ByteSizeExtensions
{
    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>
    /// Formats a byte size into a human-readable string.
    /// </summary>
    /// <param name="bytes">The number of bytes.</param>
    /// <param name="decimalPlaces">Number of decimal places to show.</param>
    /// <returns>Formatted string (e.g., "1.5 GB").</returns>
    public static string ToHumanReadableSize(this long bytes, int decimalPlaces = 2)
    {
        if (bytes == 0) return "0 B";

        int unitIndex = 0;
        double size = bytes;

        while (size >= 1024 && unitIndex < SizeUnits.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{Math.Round(size, decimalPlaces)} {SizeUnits[unitIndex]}";
    }

    /// <summary>
    /// Formats bytes per second into a human-readable speed string.
    /// </summary>
    /// <param name="bytesPerSecond">The speed in bytes per second.</param>
    /// <param name="decimalPlaces">Number of decimal places to show.</param>
    /// <returns>Formatted string (e.g., "125.5 MB/s").</returns>
    public static string ToHumanReadableSpeed(this long bytesPerSecond, int decimalPlaces = 2)
    {
        return $"{ToHumanReadableSize(bytesPerSecond, decimalPlaces)}/s";
    }
}
