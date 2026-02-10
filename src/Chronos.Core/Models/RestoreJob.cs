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
}
