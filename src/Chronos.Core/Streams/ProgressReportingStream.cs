using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Chronos.Core.Progress;

namespace Chronos.Core.Streams;

/// <summary>
/// Wraps a stream and reports progress as data is read.
/// </summary>
public sealed class ProgressReportingStream : Stream
{
    private readonly Stream _inner;
    private readonly long _totalBytes;
    private readonly IProgressReporter? _progressReporter;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastReported;
    private const long ReportIntervalBytes = 10 * 1024 * 1024; // 10 MB
    private const double ReportIntervalSeconds = 0.5;

    public ProgressReportingStream(Stream inner, ulong totalBytes, IProgressReporter? progressReporter)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _totalBytes = (long)totalBytes;
        _progressReporter = progressReporter;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        ReportProgress(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        ReportProgress(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken);
        ReportProgress(read);
        return read;
    }

    private void ReportProgress(int bytesRead)
    {
        if (bytesRead <= 0 || _progressReporter == null) return;

        long position = _inner.Position;
        double elapsed = _sw.Elapsed.TotalSeconds;

        if (elapsed > ReportIntervalSeconds && position - _lastReported > ReportIntervalBytes)
        {
            _lastReported = position;
            long bytesPerSecond = elapsed > 0 ? (long)(position / elapsed) : 0;
            double percent = _totalBytes > 0 ? (double)position / _totalBytes * 100 : 0;
            double remainingSeconds = bytesPerSecond > 0 ? (_totalBytes - position) / (double)bytesPerSecond : 0;

            _progressReporter.Report(new OperationProgress
            {
                StatusMessage = "Compressing...",
                BytesProcessed = position,
                TotalBytes = _totalBytes,
                PercentComplete = Math.Min(100, percent),
                BytesPerSecond = bytesPerSecond,
                TimeRemaining = TimeSpan.FromSeconds(remainingSeconds)
            });
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void Flush() => _inner.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
