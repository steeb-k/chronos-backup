using ZstdSharp;

namespace Chronos.Core.Compression;

/// <summary>
/// Compression provider using Zstandard (via ZstdSharp).
/// </summary>
public class ZstdCompressionProvider : ICompressionProvider
{
    private const int DefaultBufferSize = 81920; // 80 KB

    public async Task CompressAsync(Stream source, Stream destination, int compressionLevel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        // Clamp compression level to Zstd valid range (1-22)
        compressionLevel = Math.Clamp(compressionLevel, 1, 22);

        using var compressor = new CompressionStream(destination, compressionLevel, leaveOpen: true);
        await source.CopyToAsync(compressor, DefaultBufferSize, cancellationToken);
        await compressor.FlushAsync(cancellationToken);
    }

    public async Task DecompressAsync(Stream source, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        using var decompressor = new DecompressionStream(source, leaveOpen: true);
        await decompressor.CopyToAsync(destination, DefaultBufferSize, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }
}
