namespace SpaceLink.ChannelCoding;

/// <summary>
/// GF(2^8) 산술 — 리드-솔로몬 부호가 쓰는 유한체.
/// 체 생성 다항식은 CCSDS 131.0-B 가 정한 F(x) = x^8 + x^7 + x^2 + x + 1 (0x187).
/// 원시 다항식이므로 α 의 거듭제곱이 0 이 아닌 원소 255 개를 한 번씩 모두 훑는다 (시험으로 고정한다).
/// </summary>
public static class GaloisField256
{
    /// <summary>체 생성 다항식 x^8 + x^7 + x^2 + x + 1.</summary>
    public const int FieldGeneratorPolynomial = 0x187;

    /// <summary>0 이 아닌 원소의 개수 = α 의 주기.</summary>
    public const int NonZeroElements = 255;

    private static readonly byte[] ExpTable = new byte[NonZeroElements * 2];
    private static readonly byte[] LogTable = new byte[256];

    static GaloisField256()
    {
        int value = 1;
        for (int power = 0; power < NonZeroElements; power++)
        {
            ExpTable[power] = (byte)value;
            LogTable[value] = (byte)power;
            value <<= 1;
            if ((value & 0x100) != 0)
            {
                value ^= FieldGeneratorPolynomial;
            }
        }

        // 곱셈에서 지수 두 개를 더한 값(최대 508)을 접지 않고 바로 찾으려고 한 바퀴를 더 적어 둔다.
        for (int power = NonZeroElements; power < ExpTable.Length; power++)
        {
            ExpTable[power] = ExpTable[power - NonZeroElements];
        }
    }

    /// <summary>α^power. 지수는 255 로 접는다 (음수도 받는다).</summary>
    public static byte Exp(int power)
    {
        power %= NonZeroElements;
        if (power < 0)
        {
            power += NonZeroElements;
        }

        return ExpTable[power];
    }

    /// <summary>log_α(value). 0 은 로그가 없다.</summary>
    public static int Log(byte value)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "0 has no logarithm in GF(256)");
        }

        return LogTable[value];
    }

    public static byte Multiply(byte a, byte b) =>
        a == 0 || b == 0 ? (byte)0 : ExpTable[LogTable[a] + LogTable[b]];

    public static byte Divide(byte a, byte b)
    {
        if (b == 0)
        {
            throw new DivideByZeroException("division by zero in GF(256)");
        }

        return a == 0 ? (byte)0 : ExpTable[LogTable[a] - LogTable[b] + NonZeroElements];
    }

    /// <summary>a 의 곱셈 역원.</summary>
    public static byte Inverse(byte a)
    {
        if (a == 0)
        {
            throw new DivideByZeroException("0 has no inverse in GF(256)");
        }

        return ExpTable[NonZeroElements - LogTable[a]];
    }
}
