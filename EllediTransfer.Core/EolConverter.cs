namespace EllediTransfer;

/// <summary>
/// Streaming line-ending conversion used by ASCII transfer mode:
/// uploads convert CRLF to LF, downloads convert LF to CRLF.
/// </summary>
internal sealed class EolConverter(bool toCrLf)
{
    private bool _pendingCr;
    private byte _previous;

    /// <summary>Converts <paramref name="input"/> into <paramref name="output"/>, which must be twice as large.</summary>
    public int Convert(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var count = 0;
        foreach (var b in input)
        {
            if (toCrLf)
            {
                if (b == '\n' && _previous != '\r')
                {
                    output[count++] = (byte)'\r';
                }
                output[count++] = b;
                _previous = b;
            }
            else
            {
                if (_pendingCr)
                {
                    _pendingCr = false;
                    if (b != '\n')
                    {
                        output[count++] = (byte)'\r';
                    }
                }
                if (b == '\r')
                {
                    _pendingCr = true;
                }
                else
                {
                    output[count++] = b;
                }
            }
        }
        return count;
    }

    public int Flush(Span<byte> output)
    {
        if (_pendingCr)
        {
            _pendingCr = false;
            output[0] = (byte)'\r';
            return 1;
        }
        return 0;
    }
}
