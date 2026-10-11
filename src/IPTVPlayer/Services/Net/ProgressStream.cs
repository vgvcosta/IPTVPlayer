namespace IPTVPlayer.Services.Net;

/// <summary>Stream só de leitura que reporta o número total de bytes lidos.</summary>
internal sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _onProgress;
    private long _total;

    public ProgressStream(Stream inner, Action<long> onProgress)
    {
        _inner = inner;
        _onProgress = onProgress;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _total;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Report(_inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Report(_inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Report(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    private int Report(int read)
    {
        if (read > 0)
        {
            _total += read;
            _onProgress(_total);
        }
        return read;
    }
}
