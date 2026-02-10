using Chronos.Core.Progress;

namespace Chronos.Core.Imaging;

/// <summary>
/// Interface for image verification operations.
/// </summary>
public interface IVerificationEngine
{
    /// <summary>
    /// Verifies the integrity of a backup image.
    /// </summary>
    /// <param name="imagePath">The path to the image file.</param>
    /// <param name="progressReporter">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the image is valid and intact.</returns>
    Task<bool> VerifyImageAsync(string imagePath, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the hash of an image file.
    /// </summary>
    /// <param name="imagePath">The path to the image file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The SHA-256 hash of the image.</returns>
    Task<string> ComputeHashAsync(string imagePath, CancellationToken cancellationToken = default);
}
