using System;
using System.Buffers.Text;
using System.IO;
using System.IO.Compression;

namespace DepthView.Integrations.WeCreat.Gcode;

/// <summary>
/// Forward-only line reader over a G-code file that allocates nothing per line.
///
/// The size constraint is the whole design. A real MakeIt embossment job measured
/// 222 MB and 11,276,586 lines. A reader that produces a string per line produces
/// eleven million strings, and one that holds the file produces a quarter-gigabyte
/// array; either is enough to make the analysis unusable on the machine that has to
/// run it while a laser is running. So this hands out a <see cref="ReadOnlySpan{T}"/>
/// over a reused buffer and nothing else, and the caller is expected to extract what
/// it wants before calling <see cref="ReadLine"/> again.
///
/// Bytes rather than chars, deliberately. G-code words are ASCII, so decoding to
/// UTF-16 buys nothing but a copy, and <see cref="Utf8Parser"/> parses numbers
/// straight out of the byte span. Comment text is the only place non-ASCII could
/// appear, and that is converted on demand, once, by the caller that wants it.
/// </summary>
public sealed class GcodeStream : IDisposable
{
    public const int DefaultBufferSize = 1 << 16;

    private readonly Stream _stream;
    private readonly bool _ownsStream;

    private byte[] _buffer;
    private int _scan;          // first byte not yet returned as part of a line
    private int _end;           // one past the last valid byte in _buffer
    private int _lineStart;
    private int _lineLength;
    private bool _eof;

    /// <summary>1-based index of the line <see cref="Current"/> refers to.</summary>
    public long LineNumber { get; private set; }

    /// <summary>
    /// The line just read, without its terminator, valid only until the next call to
    /// <see cref="ReadLine"/>. Copy anything that has to outlive that.
    /// </summary>
    public ReadOnlySpan<byte> Current => _buffer.AsSpan(_lineStart, _lineLength);

    public GcodeStream(Stream stream, bool ownsStream = true, int bufferSize = DefaultBufferSize)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;
        _buffer = new byte[Math.Max(4096, bufferSize)];
    }

    /// <summary>
    /// Open a file, transparently decompressing it when it is gzipped.
    ///
    /// The archive stores captures gzipped because G-code compresses about fifteen
    /// times, and the decision to do that should not leak into every caller. The
    /// format is decided by the first two bytes rather than by the extension, so a
    /// .gc that is really gzip still opens and a .gz that is really plain text does
    /// too.
    /// </summary>
    public static GcodeStream Open(string path)
    {
        var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            DefaultBufferSize, FileOptions.SequentialScan);

        try
        {
            Span<byte> magic = stackalloc byte[2];
            int n = file.Read(magic);
            file.Seek(0, SeekOrigin.Begin);

            if (n == 2 && magic[0] == 0x1F && magic[1] == 0x8B)
                return new GcodeStream(new GZipStream(file, CompressionMode.Decompress));

            return new GcodeStream(file);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Advance to the next line. False at end of input.
    ///
    /// Handles LF and CRLF, and a final line with no terminator at all. A truncated
    /// capture ends mid-line, and returning that partial line rather than discarding
    /// it is what lets the segmenter notice and say so.
    /// </summary>
    public bool ReadLine()
    {
        while (true)
        {
            int available = _end - _scan;

            if (available > 0)
            {
                int nl = _buffer.AsSpan(_scan, available).IndexOf((byte)'\n');
                if (nl >= 0)
                {
                    Emit(_scan, nl);
                    _scan += nl + 1;
                    return true;
                }
            }

            if (_eof)
            {
                if (available <= 0) return false;

                Emit(_scan, available);
                _scan = _end;
                return true;
            }

            Fill();
        }
    }

    private void Emit(int start, int length)
    {
        // Strip a trailing CR so CRLF and LF files produce identical spans.
        if (length > 0 && _buffer[start + length - 1] == (byte)'\r')
            length--;

        _lineStart = start;
        _lineLength = length;
        LineNumber++;
    }

    private void Fill()
    {
        // Slide the unconsumed tail to the front so the buffer stays a window rather
        // than growing with the file.
        int remaining = _end - _scan;
        if (_scan > 0)
        {
            if (remaining > 0)
                Buffer.BlockCopy(_buffer, _scan, _buffer, 0, remaining);
            _scan = 0;
            _end = remaining;
        }

        // Only grow when a single line genuinely does not fit. G-code lines are tens
        // of bytes, so this should never fire; it exists so that a pathological file
        // fails by using memory rather than by silently splitting a line in half.
        if (_end == _buffer.Length)
            Array.Resize(ref _buffer, _buffer.Length * 2);

        int read = _stream.Read(_buffer, _end, _buffer.Length - _end);
        if (read <= 0) _eof = true;
        else _end += read;
    }

    public void Dispose()
    {
        if (_ownsStream) _stream.Dispose();
    }
}

/// <summary>
/// One G-code word: a letter and the number after it, as in <c>X-4.167</c> or
/// <c>S550</c>.
/// </summary>
public readonly struct GcodeWord
{
    public readonly char Letter;
    public readonly double Value;

    public GcodeWord(char letter, double value)
    {
        Letter = letter;
        Value = value;
    }

    public override string ToString() => $"{Letter}{Value}";
}

/// <summary>
/// Walks the words of one line without allocating.
///
/// Both dialects in play have to work here and they are not formatted alike. MakeIt
/// runs words together with no separator at all (<c>M107X-105Y-105</c>,
/// <c>G1X12.3Y4.5S550</c>); LightBurn puts a space after the command but not inside
/// the operands (<c>G1 X-4.167S0F10000</c>). Treating whitespace as optional
/// everywhere reads both, and costs nothing.
///
/// Comments are stripped before word parsing. A semicolon starts one, per both
/// dialects. Parenthesised comments are a convention in other flavours of G-code and
/// have not been seen in either file here, so they are NOT treated as comments -
/// guessing that would silently swallow a line if the assumption were wrong, and no
/// sample justifies it.
/// </summary>
public ref struct GcodeWords
{
    private ReadOnlySpan<byte> _rest;

    public GcodeWord Current { get; private set; }

    public GcodeWords(ReadOnlySpan<byte> line)
    {
        _rest = StripComment(line);
        Current = default;
    }

    /// <summary>The line with any trailing comment removed.</summary>
    public static ReadOnlySpan<byte> StripComment(ReadOnlySpan<byte> line)
    {
        int semi = line.IndexOf((byte)';');
        return semi >= 0 ? line[..semi] : line;
    }

    /// <summary>The comment text, without the semicolon, or empty when there is none.</summary>
    public static ReadOnlySpan<byte> CommentOf(ReadOnlySpan<byte> line)
    {
        int semi = line.IndexOf((byte)';');
        return semi >= 0 ? line[(semi + 1)..] : default;
    }

    public GcodeWords GetEnumerator() => this;

    public bool MoveNext()
    {
        while (_rest.Length > 0)
        {
            byte b = _rest[0];

            if (b == (byte)' ' || b == (byte)'\t')
            {
                _rest = _rest[1..];
                continue;
            }

            char letter = char.ToUpperInvariant((char)b);
            if (letter is < 'A' or > 'Z')
            {
                // Not a word start. Skip it rather than throwing: a malformed byte in
                // the middle of an eleven-million-line file should cost that one word,
                // not the whole analysis.
                _rest = _rest[1..];
                continue;
            }

            var operand = _rest[1..];

            // Allow whitespace between the letter and its number, which LightBurn does
            // not emit but a hand-edited file might.
            int skip = 0;
            while (skip < operand.Length && (operand[skip] == (byte)' ' || operand[skip] == (byte)'\t'))
                skip++;
            operand = operand[skip..];

            if (Utf8Parser.TryParse(operand, out double value, out int consumed))
            {
                Current = new GcodeWord(letter, value);
                _rest = operand[consumed..];
                return true;
            }

            // A letter with no parsable number after it. G-code has bare-letter words
            // in some dialects; neither of ours emits one, so it is dropped rather
            // than invented into a zero.
            _rest = operand;
        }

        return false;
    }
}
