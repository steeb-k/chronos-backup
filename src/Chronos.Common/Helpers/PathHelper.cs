namespace Chronos.Common.Helpers;

/// <summary>
/// Helper methods for path operations.
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// Ensures a path ends with .vhdx extension.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>The path with .vhdx extension.</returns>
    public static string EnsureVhdxExtension(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        return Path.GetExtension(path).Equals(".vhdx", StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.ChangeExtension(path, ".vhdx");
    }

    /// <summary>
    /// Checks if a path is a valid disk device path.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>True if it's a valid disk device path.</returns>
    public static bool IsValidDiskPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return path.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a path is a valid volume device path.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>True if it's a valid volume device path.</returns>
    public static bool IsValidVolumePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase);
    }
}
