namespace Chatap.Core.Tests;

// Test double: acts like a network stream that delivers only 1 byte per read call.
// The codec must still decode whole messages.
sealed class OneByteStream(byte[] data) : Stream
{
    readonly MemoryStream _inner = new(data);

    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, Math.Min(count, 1));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) =>
        _inner.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancel);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
