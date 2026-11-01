namespace Nightwave.Tuners.Dvb.Descrambling.Csa;

/// <summary>
/// DVB-CSA one packet at a time, written the way the cipher is described rather than the way it
/// is fast. The batched implementation is checked against this, and this against the published
/// vectors. Also used where a batch would be a single packet, and to scramble in the tests.
/// </summary>
/// <remarks>
/// <para>
/// A payload of <c>n</c> bytes is <c>m = n / 8</c> blocks and <c>n mod 8</c> residue bytes.
/// Payloads shorter than 8 bytes are not scrambled at all. Scrambling runs the block cipher
/// backwards over the blocks, each block XORed with the cipher text of the one after it before
/// it is encrypted (the last with nothing), and then XORs the stream cipher over everything
/// after the first block, the stream cipher being initialised with that first cipher block.
/// Descrambling undoes the two in the opposite order.
/// </para>
/// </remarks>
internal static class CsaReference
{
    public static void Descramble(CsaKey key, Span<byte> payload)
    {
        if (payload.Length < 8)
        {
            return;
        }

        XorKeystream(key.ControlWord, payload[..8], payload[8..]);

        var blocks = payload.Length / 8;
        Span<byte> decrypted = stackalloc byte[8];
        for (var k = 0; k < blocks; k++)
        {
            var block = payload.Slice(k * 8, 8);
            DecryptBlock(key.BlockSchedule, block, decrypted);
            if (k + 1 < blocks)
            {
                // The cipher text of the next block is still in place: it is what this block was
                // chained with.
                var next = payload.Slice((k + 1) * 8, 8);
                for (var i = 0; i < 8; i++)
                {
                    decrypted[i] ^= next[i];
                }
            }

            decrypted.CopyTo(block);
        }
    }

    public static void Scramble(CsaKey key, Span<byte> payload)
    {
        if (payload.Length < 8)
        {
            return;
        }

        var blocks = payload.Length / 8;
        Span<byte> chained = stackalloc byte[8];
        for (var k = blocks - 1; k >= 0; k--)
        {
            var block = payload.Slice(k * 8, 8);
            block.CopyTo(chained);
            if (k + 1 < blocks)
            {
                var next = payload.Slice((k + 1) * 8, 8);
                for (var i = 0; i < 8; i++)
                {
                    chained[i] ^= next[i];
                }
            }

            EncryptBlock(key.BlockSchedule, chained, block);
        }

        XorKeystream(key.ControlWord, payload[..8], payload[8..]);
    }

    /// <summary>
    /// One decryption round, applied for key bytes 55 down to 0, with <c>S = sbox(k ^ W6)</c>:
    /// <c>W0..W7 := (W7^S, W0, W1^L, W2^L, W3^L, W4, W5^perm(S), W6)</c> where <c>L = W7^S</c>.
    /// </summary>
    public static void DecryptBlock(ReadOnlySpan<byte> schedule, ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> w = stackalloc byte[8];
        input[..8].CopyTo(w);
        var sbox = CsaTables.BlockSbox;

        for (var round = CsaTables.BlockRounds - 1; round >= 0; round--)
        {
            var s = sbox[schedule[round] ^ w[6]];
            var l = (byte)(w[7] ^ s);

            w[7] = w[6];
            w[6] = (byte)(w[5] ^ CsaTables.BlockPermutation[s]);
            w[5] = w[4];
            w[4] = (byte)(w[3] ^ l);
            w[3] = (byte)(w[2] ^ l);
            w[2] = (byte)(w[1] ^ l);
            w[1] = w[0];
            w[0] = l;
        }

        w.CopyTo(output);
    }

    /// <summary>The inverse of <see cref="DecryptBlock"/>, rounds 0 to 55.</summary>
    public static void EncryptBlock(ReadOnlySpan<byte> schedule, ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> w = stackalloc byte[8];
        input[..8].CopyTo(w);
        var sbox = CsaTables.BlockSbox;

        for (var round = 0; round < CsaTables.BlockRounds; round++)
        {
            // w is the state after a decryption round; w[7] is the W6 that round started from.
            var s = sbox[schedule[round] ^ w[7]];
            var l = w[0];

            var w0 = w[1];
            var w1 = (byte)(w[2] ^ l);
            var w2 = (byte)(w[3] ^ l);
            var w3 = (byte)(w[4] ^ l);
            var w4 = w[5];
            var w5 = (byte)(w[6] ^ CsaTables.BlockPermutation[s]);
            var w6 = w[7];
            var w7 = (byte)(l ^ s);

            w[0] = w0;
            w[1] = w1;
            w[2] = w2;
            w[3] = w3;
            w[4] = w4;
            w[5] = w5;
            w[6] = w6;
            w[7] = w7;
        }

        w.CopyTo(output);
    }

    /// <summary>
    /// Runs the stream cipher: loads the control word, clocks in the 8 initialisation bytes
    /// (4 clocks each), then produces 2 bits per clock, most significant pair first, and XORs
    /// them over <paramref name="data"/>.
    /// </summary>
    public static void XorKeystream(ReadOnlySpan<byte> controlWord, ReadOnlySpan<byte> iv, Span<byte> data)
    {
        var state = new StreamState(controlWord);

        for (var i = 0; i < 8; i++)
        {
            var high = iv[i] >> 4;
            var low = iv[i] & 0xf;

            // Each byte goes in four times: high nibble to A and low nibble to B, then the other
            // way round, twice.
            state.Clock(high, low, initialising: true);
            state.Clock(low, high, initialising: true);
            state.Clock(high, low, initialising: true);
            state.Clock(low, high, initialising: true);
        }

        for (var i = 0; i < data.Length; i++)
        {
            var keystream = 0;
            for (var pair = 0; pair < 4; pair++)
            {
                keystream = (keystream << 2) | state.Clock(0, 0, initialising: false);
            }

            data[i] ^= (byte)keystream;
        }
    }

    /// <summary>The stream cipher's state, one 4 bit cell per array element.</summary>
    private sealed class StreamState
    {
        // a[0] is A1, a[9] is A10.
        private readonly int[] _a = new int[10];
        private readonly int[] _b = new int[10];
        private int _x;
        private int _y;
        private int _z;
        private int _d;
        private int _e;
        private int _f;
        private int _p;
        private int _q;
        private int _r;

        public StreamState(ReadOnlySpan<byte> controlWord)
        {
            // A1..A8 are the nibbles of control word bytes 0-3, B1..B8 of bytes 4-7, high nibble
            // first. A9, A10, B9 and B10 start at zero, as does everything else.
            for (var i = 0; i < 4; i++)
            {
                _a[2 * i] = controlWord[i] >> 4;
                _a[(2 * i) + 1] = controlWord[i] & 0xf;
                _b[2 * i] = controlWord[i + 4] >> 4;
                _b[(2 * i) + 1] = controlWord[i + 4] & 0xf;
            }
        }

        /// <summary>One clock. Returns the two output bits, which only mean something once initialised.</summary>
        public int Clock(int inputA, int inputB, bool initialising)
        {
            var nextA = _a[9] ^ _x;
            var nextB = _b[6] ^ _b[9] ^ _y;
            if (initialising)
            {
                nextA ^= _d ^ inputA;
                nextB ^= inputB;
            }

            if (_p != 0)
            {
                nextB = ((nextB << 1) | (nextB >> 3)) & 0xf;
            }

            Array.Copy(_a, 0, _a, 1, 9);
            Array.Copy(_b, 0, _b, 1, 9);
            _a[0] = nextA;
            _b[0] = nextB;

            // The combiner works with the S-box outputs of the previous clock.
            var nextD = _e ^ _z ^ SelectFromB();
            var nextE = _f;
            var nextF = _e;
            var nextR = _r;
            if (_q != 0)
            {
                var sum = _z + _e + _r;
                nextF = sum & 0xf;
                nextR = sum >> 4;
            }

            _d = nextD;
            _e = nextE;
            _f = nextF;
            _r = nextR;

            ApplySboxes();

            return (((_d >> 3) ^ (_d >> 2)) & 1) << 1 | (((_d >> 1) ^ _d) & 1);
        }

        private int SelectFromB()
        {
            var selected = 0;
            for (var bit = 0; bit < 4; bit++)
            {
                var value = 0;
                foreach (var (cell, cellBit) in CsaTables.StreamBSelection[bit])
                {
                    value ^= (_b[cell - 1] >> cellBit) & 1;
                }

                selected |= value << bit;
            }

            return selected;
        }

        private void ApplySboxes()
        {
            _x = _y = _z = _p = _q = 0;
            foreach (var sbox in CsaTables.StreamSboxes)
            {
                var index = 0;
                for (var j = 0; j < 5; j++)
                {
                    var (cell, bit) = sbox.Inputs[j];
                    index |= ((_a[cell - 1] >> bit) & 1) << j;
                }

                var value = sbox.Table[index];
                Set(sbox.Outputs[0], value & 1);
                Set(sbox.Outputs[1], (value >> 1) & 1);
            }
        }

        private void Set(CsaTables.StreamOutput output, int bit)
        {
            switch (output)
            {
                case <= CsaTables.StreamOutput.X3:
                    _x |= bit << (output - CsaTables.StreamOutput.X0);
                    break;
                case <= CsaTables.StreamOutput.Y3:
                    _y |= bit << (output - CsaTables.StreamOutput.Y0);
                    break;
                case <= CsaTables.StreamOutput.Z3:
                    _z |= bit << (output - CsaTables.StreamOutput.Z0);
                    break;
                case CsaTables.StreamOutput.P:
                    _p = bit;
                    break;
                default:
                    _q = bit;
                    break;
            }
        }
    }
}
