namespace Chronos.Core.Models;

/// <summary>
/// Represents metadata stored with a backup image.
/// </summary>
public class ImageMetadata
{
    /// <summary>
    /// Gets or sets the version of Chronos that created this image.
    /// </summary>
    public required string ChronosVersion { get; set; }

    /// <summary>
    /// Gets or sets the creation timestamp.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the source disk/partition identifier.
    /// </summary>
    public required string SourceIdentifier { get; set; }

    /// <summary>
    /// Gets or sets the original size in bytes.
    /// </summary>
    public long OriginalSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the compressed size in bytes.
    /// </summary>
    public long CompressedSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the compression algorithm used.
    /// </summary>
    public string CompressionAlgorithm { get; set; } = "Zstandard";

    /// <summary>
    /// Gets or sets the compression level.
    /// </summary>
    public int CompressionLevel { get; set; }

    /// <summary>
    /// Gets or sets the SHA-256 hash of the image.
    /// </summary>
    public string? Hash { get; set; }

    /// <summary>
    /// Gets or sets whether VSS was used for this backup.
    /// </summary>
    public bool UsedVSS { get; set; }

    /// <summary>
    /// Gets or sets the filesystem type.
    /// </summary>
    public string? FilesystemType { get; set; }

    /// <summary>
    /// Gets or sets an optional user description.
    /// </summary>
    public string? Description { get; set; }
}
