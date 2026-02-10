namespace Chronos.Core.Progress;

/// <summary>
/// Interface for reporting operation progress.
/// </summary>
public interface IProgressReporter
{
    /// <summary>
    /// Reports progress of an ongoing operation.
    /// </summary>
    /// <param name="progress">The operation progress information.</param>
    void Report(OperationProgress progress);
}
