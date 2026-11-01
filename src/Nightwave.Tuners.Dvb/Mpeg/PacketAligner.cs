namespace Nightwave.Tuners.Dvb.Mpeg;

/// <summary>
/// Cuts a byte stream into whole transport stream packets. RTP payloads are whole packets
/// already, but an HTTP body arrives in whatever pieces the network made of it, and a stream
/// that lost bytes has to find the sync byte again rather than hand out shifted garbage.
/// </summary>
internal sealed class PacketAligner
{
    private readonly byte[] _carry = new byte[TransportPacket.Size];
    private byte[] _scratch = [];
    private int _carryLength;

    /// <summary>
    /// Takes the next piece of the stream and writes the whole packets it completes into
    /// <paramref name="output"/>, which must hold <c>data.Length + 188</c> bytes. Returns how many
    /// bytes were written, always a multiple of 188.
    /// </summary>
    public int Push(ReadOnlySpan<byte> data, Span<byte> output)
    {
        ReadOnlySpan<byte> input = data;

        if (_carryLength > 0)
        {
            // What was left over is looked at again together with what follows: a sync byte that
            // seemed right at the end of the last piece may turn out not to be.
            var length = _carryLength + data.Length;
            if (_scratch.Length < length)
            {
                _scratch = new byte[Math.Max(length, 2 * _scratch.Length)];
            }

            _carry.AsSpan(0, _carryLength).CopyTo(_scratch);
            data.CopyTo(_scratch.AsSpan(_carryLength));
            input = _scratch.AsSpan(0, length);
            _carryLength = 0;
        }

        var written = 0;
        var position = 0;

        while (position < input.Length)
        {
            if (input[position] != TransportPacket.SyncByte || !IsInStep(input, position))
            {
                position++;
                continue;
            }

            if (input.Length - position < TransportPacket.Size)
            {
                input[position..].CopyTo(_carry);
                _carryLength = input.Length - position;
                break;
            }

            input.Slice(position, TransportPacket.Size).CopyTo(output[written..]);
            written += TransportPacket.Size;
            position += TransportPacket.Size;
        }

        return written;
    }

    /// <summary>
    /// A sync byte is believed when the next packet starts one packet later, or when the data
    /// ends before that could be seen.
    /// </summary>
    private static bool IsInStep(ReadOnlySpan<byte> data, int position)
    {
        var next = position + TransportPacket.Size;

        return next >= data.Length || data[next] == TransportPacket.SyncByte;
    }
}
