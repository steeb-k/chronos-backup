namespace Chronos.Core.Models;

/// <summary>
/// Represents a backup operation job configuration.
/// </summary>
public class BackupJob
{
    /// <summary>
    /// Gets or sets the source disk or partition path.
    /// </summary>
    public required string SourcePath { get; set; }

    /// <summary>
    /// Gets or sets the destination VHDX file path.
    /// </summary>
    public required string DestinationPath { get; set; }

    /// <summary>
    /// Gets or sets the backup type.
    /// </summary>
    public BackupType Type { get; set; }

    /// <summary>
    /// Gets or sets the compression level (0-22 for Zstandard).
    /// </summary>
    public int CompressionLevel { get; set; } = 3;

    /// <summary>
    /// Gets or sets whether to use VSS for consistent snapshots.
    /// </summary>
    public bool UseVSS { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to verify the backup after creation.
    /// </summary>
    public bool VerifyAfterBackup { get; set; } = true;

    /// <summary>
    /// Gets or sets an optional description for the backup.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Defines the type of backup operation.
/// </summary>
public enum BackupType
{
    /// <summary>
    /// Full disk backup.
    /// </summary>
    FullDisk,

    /// <summary>
    /// Single partition backup.
    /// </summary>
    Partition,

    /// <summary>
    /// Disk clone (disk-to-disk).
    /// </summary>
    DiskClone,

    /// <summary>
    /// Partition clone (partition-to-partition).
    /// </summary>
    PartitionClone
}
