namespace Mellow.SlopFactory.Infrastructure.Storage;

internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private long _remaining;

    public BoundedReadStream(Stream inner, long length)
    {
        _inner = inner;
        _length = length;
        _remaining = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
        _remaining -= read;
        return read;
    }
    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer[..Math.Min(buffer.Length, (int)Math.Min(int.MaxValue, _remaining))]);
        _remaining -= read;
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer[..Math.Min(buffer.Length, (int)Math.Min(int.MaxValue, _remaining))], cancellationToken).ConfigureAwait(false);
        _remaining -= read;
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
