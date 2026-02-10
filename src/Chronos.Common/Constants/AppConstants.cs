namespace Chronos.Common.Constants;

/// <summary>
/// Application-wide constants.
/// </summary>
public static class AppConstants
{
    /// <summary>
    /// Application name.
    /// </summary>
    public const string AppName = "Chronos";

    /// <summary>
    /// Application version.
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// Default compression level for Zstandard (1-22).
    /// </summary>
    public const int DefaultCompressionLevel = 3;

    /// <summary>
    /// Buffer size for I/O operations (4 MB).
    /// </summary>
    public const int BufferSize = 4 * 1024 * 1024;

    /// <summary>
    /// Default VHDX file extension.
    /// </summary>
    public const string VhdxExtension = ".vhdx";

    /// <summary>
    /// Metadata file name stored within images.
    /// </summary>
    public const string MetadataFileName = "chronos.metadata.json";
}
