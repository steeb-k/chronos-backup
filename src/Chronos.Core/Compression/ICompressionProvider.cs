namespace Chronos.Core.Compression;

/// <summary>
/// Interface for compression providers.
/// </summary>
public interface ICompressionProvider
{
    /// <summary>
    /// Compresses data from the source stream to the destination stream.
    /// </summary>
    /// <param name="source">The source stream to compress.</param>
    /// <param name="destination">The destination stream for compressed data.</param>
    /// <param name="compressionLevel">The compression level (algorithm-specific).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when compression is finished.</returns>
    Task CompressAsync(Stream source, Stream destination, int compressionLevel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decompresses data from the source stream to the destination stream.
    /// </summary>
    /// <param name="source">The source stream to decompress.</param>
    /// <param name="destination">The destination stream for decompressed data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when decompression is finished.</returns>
    Task DecompressAsync(Stream source, Stream destination, CancellationToken cancellationToken = default);
}
