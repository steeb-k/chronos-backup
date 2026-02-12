namespace Chronos.Core.Models;

/// <summary>
/// Represents a restore operation job configuration.
/// </summary>
public class RestoreJob
{
    /// <summary>
    /// Gets or sets the source VHDX file path.
    /// </summary>
    public required string SourceImagePath { get; set; }

    /// <summary>
    /// Gets or sets the target disk or partition path.
    /// </summary>
    public required string TargetPath { get; set; }

    /// <summary>
    /// Gets or sets whether to verify sectors during restore.
    /// </summary>
    public bool VerifyDuringRestore { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to force overwrite without confirmation.
    /// </summary>
    public bool ForceOverwrite { get; set; }

    /// <summary>
    /// When set, restores only this single partition from the source image
    /// instead of the entire disk. The value is the 1-based partition number
    /// as it appears inside the attached VHDX.
    /// </summary>
    public uint? SourcePartitionNumber { get; set; }

    /// <summary>
    /// When restoring a single partition to unallocated space on the target disk,
    /// this is the byte offset where the unallocated region starts.
    /// If null, the target is an existing partition (identified by TargetPath).
    /// </summary>
    public ulong? TargetUnallocatedOffset { get; set; }

    /// <summary>
    /// Size (bytes) of the target unallocated region. Used together with
    /// <see cref="TargetUnallocatedOffset"/> when creating a new partition.
    /// </summary>
    public ulong? TargetUnallocatedSize { get; set; }
}
