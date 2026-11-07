using System.Collections.Concurrent;

namespace KuzushiClassifierApp.Plugin.Tests;

/// <summary>
/// One direction of an in-memory duplex pipe: writes queue chunks that the
/// other end reads. Completing the writer surfaces EOF (a zero-byte read) to
/// the reader once the queue drains — exactly like stdin closing.
/// </summary>
internal sealed class MemoryDuplexStream : Stream
{
    private readonly BlockingCollection<byte[]> _chunks = new();
    private byte[]? _current;
    private int _offset;

    public void CompleteWriting() => _chunks.CompleteAdding();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        while (_current is null || _offset >= _current.Length)
        {
            byte[]? next;
            try
            {
                next = await Task.Run(
                    () => _chunks.TryTake(out var chunk, Timeout.Infinite, cancellationToken)
                        ? chunk
                        : null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            if (next is null)
            {
                return 0; // completed and drained: EOF
            }

            _current = next;
            _offset = 0;
        }

        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsSpan(_offset, count).CopyTo(buffer.Span);
        _offset += count;
        return count;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
            .ConfigureAwait(false);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        _chunks.Add(buffer.ToArray(), cancellationToken);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _chunks.Add(buffer.AsMemory(offset, count).ToArray(), cancellationToken);
        return Task.CompletedTask;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        _chunks.Add(buffer.AsMemory(offset, count).ToArray());
}
