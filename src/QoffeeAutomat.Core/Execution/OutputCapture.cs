using System.Text;

namespace QoffeeAutomat.Core.Execution;

/// <summary>
/// Keeps the first <c>limit</c> bytes of a process stream as UTF-8 text and remembers that more
/// arrived. A multi-byte character cut in two by the limit is dropped, not replaced.
/// </summary>
internal sealed class OutputCapture
{
    private readonly object _gate = new();
    private readonly int _limit;
    private readonly Decoder _decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetDecoder();
    private readonly StringBuilder _text = new();
    private int _bytes;
    private bool _truncated;

    public OutputCapture(int limit)
    {
        _limit = limit;
    }

    public bool IsTruncated
    {
        get
        {
            lock (_gate)
            {
                return _truncated;
            }
        }
    }

    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }

    /// <summary>Adds bytes read from the stream.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The text that was kept, or null when nothing was (the limit was reached).</returns>
    public string? Append(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            int room = _limit - _bytes;
            if (data.Length > room)
            {
                _truncated = true;
            }

            if (room <= 0 || data.IsEmpty)
            {
                return null;
            }

            ReadOnlySpan<byte> keep = data.Length <= room ? data : data[..room];
            _bytes += keep.Length;

            char[] chars = new char[_decoder.GetCharCount(keep, flush: false)];
            int count = _decoder.GetChars(keep, chars, flush: false);
            string text = new(chars, 0, count);
            _text.Append(text);
            return text;
        }
    }
}
