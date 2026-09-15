namespace SpaceLink;

/// <summary>
/// CRC-16/CCITT-FALSE — CCSDS TM 전송 프레임의 FECF(Frame Error Control Field).
/// 다항식 0x1021, 초기값 0xFFFF, 비트 반전 없음, 최종 XOR 없음. 검사값: "123456789" → 0x29B1.
/// </summary>
public static class Crc16Ccitt
{
    public const ushort Polynomial = 0x1021;
    public const ushort InitialValue = 0xFFFF;

    private static readonly ushort[] Table = BuildTable();

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = InitialValue;
        foreach (byte b in data)
        {
            crc = (ushort)((crc << 8) ^ Table[((crc >> 8) ^ b) & 0xFF]);
        }

        return crc;
    }

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < table.Length; i++)
        {
            ushort value = (ushort)(i << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 0x8000) != 0 ? (ushort)((value << 1) ^ Polynomial) : (ushort)(value << 1);
            }

            table[i] = value;
        }

        return table;
    }
}
