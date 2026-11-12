using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace Glider.Core.Legendary;

internal delegate void LineHandler(ReadOnlySpan<char> line);

/// <summary>
/// Reads a child-process stream through a <see cref="PipeReader"/>, splits it on CR or LF (verify progress uses bare CR,
/// Windows text mode adds CRLF), decodes each line into a reused char buffer and hands it to the handler as a span.
/// Steady state allocates nothing per line; the handler decides what (if anything) to keep.
/// </summary>
internal sealed class LinePump
{
    /// <summary>A line longer than this is delivered in pieces (protects memory against a stream without newlines).</summary>
    public const int MaxLineBytes = 1 << 20;

    private static ReadOnlySpan<byte> Delimiters => "\r\n"u8;

    private readonly Encoding _encoding;
    private readonly bool _unescapePython;
    private readonly LineHandler _handler;
    private char[] _chars = new char[1024];
    private byte[] _bytes = new byte[1024];

    public LinePump(Encoding encoding, bool unescapePython, LineHandler handler)
    {
        _encoding = encoding;
        _unescapePython = unescapePython;
        _handler = handler;
    }

    public async Task RunAsync(Stream stream, CancellationToken ct)
    {
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 16 * 1024, minimumReadSize: 4 * 1024, leaveOpen: true));
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;
                Consume(ref buffer, result.IsCompleted);
                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous core, also used by tests to feed byte sequences directly.</summary>
    internal void Consume(ref ReadOnlySequence<byte> buffer, bool final)
    {
        while (true)
        {
            var sr = new SequenceReader<byte>(buffer);
            if (sr.TryReadToAny(out ReadOnlySequence<byte> line, Delimiters, advancePastDelimiter: true))
            {
                Deliver(line);
                buffer = buffer.Slice(sr.Position);
                continue;
            }

            if (buffer.Length >= MaxLineBytes)
            {
                Deliver(buffer.Slice(0, MaxLineBytes));
                buffer = buffer.Slice(MaxLineBytes);
                continue;
            }

            if (final && !buffer.IsEmpty)
            {
                Deliver(buffer);
                buffer = buffer.Slice(buffer.End);
            }

            return;
        }
    }

    private void Deliver(ReadOnlySequence<byte> line)
    {
        if (line.IsEmpty)
        {
            return; // the LF of a CRLF pair, or a blank line
        }

        ReadOnlySpan<byte> bytes;
        if (line.IsSingleSegment)
        {
            bytes = line.FirstSpan;
        }
        else
        {
            var len = (int)line.Length;
            if (_bytes.Length < len)
            {
                _bytes = new byte[Math.Max(len, _bytes.Length * 2)];
            }

            line.CopyTo(_bytes);
            bytes = _bytes.AsSpan(0, len);
        }

        var max = _encoding.GetMaxCharCount(bytes.Length);
        if (_chars.Length < max)
        {
            _chars = new char[Math.Max(max, _chars.Length * 2)];
        }

        var n = _encoding.GetChars(bytes, _chars);
        if (_unescapePython)
        {
            n = LegendaryEncoding.UnescapeBackslashReplace(_chars.AsSpan(0, n), _encoding);
        }

        _handler(_chars.AsSpan(0, n));
    }
}
