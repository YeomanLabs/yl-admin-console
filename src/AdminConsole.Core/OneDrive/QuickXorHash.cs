namespace AdminConsole.Core.OneDrive;

/// <summary>
/// OneDrive for Business's file hash (<c>file.hashes.quickXorHash</c>), so an export can prove each file arrived intact
/// rather than merely that something of the right size did (M16.4). Microsoft's published reference algorithm: a 160-bit
/// XOR of the content shifted 11 bits per byte, with the length XORed into the last eight bytes.
/// </summary>
public sealed class QuickXorHash
{
    private const int BitsInLastCell = 32;
    private const int Shift = 11;
    private const int WidthInBits = 160;

    private readonly ulong[] _data = new ulong[(WidthInBits - 1) / 64 + 1];
    private long _length;
    private int _shiftSoFar;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        var count = bytes.Length;
        var vectorIndex = _shiftSoFar / 64;
        var vectorOffset = _shiftSoFar % 64;
        var iterations = Math.Min(count, WidthInBits);

        for (var i = 0; i < iterations; i++)
        {
            var isLastCell = vectorIndex == _data.Length - 1;
            var bitsInCell = isLastCell ? BitsInLastCell : 64;

            if (vectorOffset <= bitsInCell - 8)
            {
                for (var j = i; j < count; j += WidthInBits) _data[vectorIndex] ^= (ulong)bytes[j] << vectorOffset;
            }
            else
            {
                var index2 = isLastCell ? 0 : vectorIndex + 1;
                var low = bitsInCell - vectorOffset;
                byte xored = 0;
                for (var j = i; j < count; j += WidthInBits) xored ^= bytes[j];
                _data[vectorIndex] ^= (ulong)xored << vectorOffset;
                _data[index2] ^= (ulong)xored >> low;
            }

            vectorOffset += Shift;
            while (vectorOffset >= bitsInCell)
            {
                vectorIndex = isLastCell ? 0 : vectorIndex + 1;
                vectorOffset -= bitsInCell;
            }
        }

        _shiftSoFar = (int)((_shiftSoFar + (long)Shift * (count % WidthInBits)) % WidthInBits);
        _length += count;
    }

    /// <summary>The hash as OneDrive reports it: base64 of 20 bytes.</summary>
    public string ToBase64()
    {
        var result = new byte[(WidthInBits - 1) / 8 + 1];
        for (var i = 0; i < _data.Length - 1; i++) BitConverter.GetBytes(_data[i]).CopyTo(result, i * 8);
        var last = BitConverter.GetBytes(_data[^1]);
        Array.Copy(last, 0, result, (_data.Length - 1) * 8, result.Length - (_data.Length - 1) * 8);

        var length = BitConverter.GetBytes(_length);
        for (var i = 0; i < length.Length; i++) result[WidthInBits / 8 - length.Length + i] ^= length[i];
        return Convert.ToBase64String(result);
    }

    public static string Of(ReadOnlySpan<byte> bytes)
    {
        var h = new QuickXorHash();
        h.Append(bytes);
        return h.ToBase64();
    }
}
