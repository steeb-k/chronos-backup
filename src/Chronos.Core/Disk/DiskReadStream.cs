using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Chronos.Core.Disk;

/// <summary>
/// A read-only stream that reads raw sectors from a disk via IDiskReader.
/// Used for compressed backup to file (piping disk data through compression).
/// </summary>
public sealed class DiskReadStream : Stream
{
    private readonly IDiskReader _reader;
    private readonly DiskReadHandle _handle;
    private readonly byte[] _buffer;
    private long _position;
    private bool _disposed;

    public DiskReadStream(IDiskReader reader, DiskReadHandle handle, int bufferSize = 1024 * 1024)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        // Align buffer to sector boundary
        int sectorAligned = (bufferSize / (int)handle.SectorSize) * (int)handle.SectorSize;
        _buffer = new byte[Math.Max(512, sectorAligned)];
        _position = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => (long)_handle.SizeBytes;
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(buffer));

        if (_position >= Length)
            return 0;

        int totalRead = 0;
        int sectorsPerRead = _buffer.Length / (int)_handle.SectorSize;

        while (count > 0 && _position < Length)
        {
            long sectorOffset = _position / _handle.SectorSize;
            int sectorCount = (int)Math.Min(sectorsPerRead, (Length - _position) / _handle.SectorSize);
            if (sectorCount == 0) break;

            int bytesRead = await _reader.ReadSectorsAsync(_handle, _buffer, sectorOffset, sectorCount, cancellationToken);
            if (bytesRead == 0) break;

            int toCopy = Math.Min(bytesRead, count);
            Array.Copy(_buffer, 0, buffer, offset, toCopy);
            _position += toCopy;
            totalRead += toCopy;
            offset += toCopy;
            count -= toCopy;

            if (bytesRead < sectorCount * _handle.SectorSize)
                break;
        }

        return totalRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
                _handle.Dispose();
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
