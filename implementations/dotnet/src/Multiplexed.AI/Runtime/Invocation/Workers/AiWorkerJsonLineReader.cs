using System.Text;

namespace Multiplexed.AI.Runtime.Invocation.Workers
{
    /// <summary>Bounded UTF-8 framing; ReadLineAsync alone would permit an unbounded line allocation.</summary>
    public sealed class AiWorkerJsonLineReader
    {
        private readonly Stream _stream;
        private readonly int _maxBytes;
        private readonly byte[] _buffer = new byte[4096];
        private int _offset, _count;
        private static readonly UTF8Encoding Utf8 = new(false, true);
        public AiWorkerJsonLineReader(Stream stream, int maxBytes)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (maxBytes is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            _maxBytes = maxBytes;
        }
        public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
        {
            using var line = new MemoryStream();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_offset == _count)
                {
                    _count = await _stream.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false); _offset = 0;
                    if (_count == 0)
                    {
                        if (line.Length != 0) throw new InvalidOperationException("Worker frame ended without a newline.");
                        return null;
                    }
                }
                var end = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
                var length = (end < 0 ? _count : end) - _offset;
                if (line.Length + length > _maxBytes) throw new InvalidOperationException("Worker frame exceeds the configured byte limit.");
                line.Write(_buffer, _offset, length); _offset += length;
                if (end < 0) continue;
                _offset++;
                var bytes = line.ToArray(); var size = bytes.Length;
                if (size > 0 && bytes[size - 1] == '\r') size--;
                if (size == 0) throw new InvalidOperationException("Empty worker frames are not allowed.");
                return Utf8.GetString(bytes, 0, size);
            }
        }
    }
}
