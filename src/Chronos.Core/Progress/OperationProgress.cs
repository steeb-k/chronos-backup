namespace Chronos.Core.Progress;

/// <summary>
/// Represents the progress of a long-running operation.
/// </summary>
public class OperationProgress
{
    /// <summary>
    /// Gets or sets the current progress percentage (0-100).
    /// </summary>
    public double PercentComplete { get; set; }

    /// <summary>
    /// Gets or sets the number of bytes processed.
    /// </summary>
    public long BytesProcessed { get; set; }

    /// <summary>
    /// Gets or sets the total number of bytes to process.
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Gets or sets the current processing speed in bytes per second.
    /// </summary>
    public long BytesPerSecond { get; set; }

    /// <summary>
    /// Gets or sets the estimated time remaining.
    /// </summary>
    public TimeSpan? TimeRemaining { get; set; }

    /// <summary>
    /// Gets or sets the current status message.
    /// </summary>
    public string? StatusMessage { get; set; }

    /// <summary>
    /// Gets or sets the current operation phase.
    /// </summary>
    public string? Phase { get; set; }
}
