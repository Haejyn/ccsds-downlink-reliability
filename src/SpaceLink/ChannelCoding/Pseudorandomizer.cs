namespace SpaceLink.ChannelCoding;

/// <summary>PN 수열의 종류 — 물리 채널마다 미리 정해 두고 전송 데이터로 알리지 않는다(§10.1).</summary>
public enum PseudorandomSequence
{
    /// <summary>표준의 기본값 — h(x) = x^17 + x^14 + 1, 131071 비트 (§10.4.1).</summary>
    Standard131071,

    /// <summary>옛 시스템 호환용 — h(x) = x^8 + x^7 + x^5 + x^3 + 1, 255 비트 (§10.4.2).</summary>
    Legacy255,
}

/// <summary>
/// 의사잡음(PN) 랜덤화 — 같은 비트가 길게 이어지면 수신기가 비트 동기를 잃으므로, 보내기 전에 고정 수열과 XOR 한다.
/// 코드블록마다 수열을 처음부터 다시 건다(§10.4.3). 두 수열 모두 표준(CCSDS 131.0-B-5 §10.4.3 NOTE 2)이 싣는
/// 처음 40 비트와 시험이 값으로 대조한다 — 131071 비트는 <c>1C 71 B9 1B A9</c>, 255 비트는 <c>FF 48 0E C0 9A</c>.
///
/// 131071 비트 수열이 표준의 기본값이다(§10.4.1 "shall"). 255 비트는 옛 시스템 호환용으로 허용될 뿐이고(§10.4.2 "may"),
/// 표준은 1/255 심볼율에 스펙트럼 선이 생길 수 있다고 경고한다. 처음에는 255 비트만 있었다.
/// </summary>
public static class Pseudorandomizer
{
    /// <summary>
    /// 131071 비트 수열의 초기값 — 표준이 적은 <c>11000111000111000</c> 을 그대로 2진 상수로 옮긴 것이다(§10.4.3).
    /// 이 레지스터에서 비트 k 가 수열의 k 번째 비트다(비트 0 이 가장 먼저 나간다). 그래서 표준의 문자열은 **오른쪽 끝부터**
    /// 나간다 — 첫 17 비트는 <c>00011100011100011</c> 이다. 문자열을 왼쪽부터 첫 비트로 읽으면 다른 수열이 된다
    /// (PN 탭을 두 번 틀린 CH-1·CH-4 와 같은 방향 함정이라, 처음 40 비트로 먼저 확인하고 옮겼다).
    /// </summary>
    public const int Standard131071Seed = 0b1_1000_1110_0011_1000;

    /// <summary>131071 비트 수열의 주기 (비트) = 2^17 − 1.</summary>
    public const int Standard131071PeriodBits = (1 << 17) - 1;

    /// <summary>
    /// 한 번에 랜덤화할 수 있는 최대 길이 (바이트) — 주기 안쪽이라 되감기 없이 앞부분만 쓴다.
    /// 표준의 최대 코드블록(I = 8, 2040 바이트)보다 넉넉히 크다.
    /// </summary>
    public const int Standard131071MaxBytes = Standard131071PeriodBits / 8;

    /// <summary>
    /// 131071 비트 수열의 앞부분을 미리 만들어 둔다 — 코드블록마다 처음부터 다시 걸기 때문에 늘 같은 앞부분만 쓴다.
    /// 점화식 s[n+17] = s[n+14] ⊕ s[n] (h(x) = x^17 + x^14 + 1). 첫 비트가 바이트의 MSB 다.
    /// </summary>
    private static readonly byte[] Standard131071Table = BuildStandard131071Table();

    /// <summary>
    /// LFSR 되먹임 탭. h(x) = x^8 + x^7 + x^5 + x^3 + 1 은 수열의 점화식
    /// s[n+8] = s[n+7] ⊕ s[n+5] ⊕ s[n+3] ⊕ s[n] 이다. 상태 바이트에 s[n] 은 비트 7(가장 오래된 것, 출력),
    /// s[n+k] 는 비트 7−k 에 있으므로 지수 7 · 5 · 3 · 0 은 비트 <b>0 · 2 · 4 · 7</b> 자리에 놓인다 (0x95).
    ///
    /// ⚠ 이 상수는 두 번 틀렸다. 처음에는 지수를 비트 번호로 그대로 옮겨(0xA9) 주기가 255 가 아니라 217 이었고(CH-1),
    /// 주기 시험이 잡아 비트 7 · 6 · 4 · 2 (0xD4) 로 "고쳤다" — 그런데 0xD4 도 주기 255 인 <b>다른</b> 다항식이라
    /// 표준의 수열이 아니었다 (<c>FF 1A AF 66 52 …</c>, 표준은 <c>FF 48 0E C0 9A …</c>). 성질 시험(자기 역원 · 주기 · 첫 바이트)은
    /// 최대 길이 LFSR 이면 어느 다항식이든 통과하므로 이 틀림을 못 잡았고, 표준 문서를 받아 처음 40 비트와 값으로 대조하고서야 드러났다(CH-4).
    /// </summary>
    public const int FeedbackTaps = 0b1001_0101;

    /// <summary>수열이 되풀이되는 길이 (바이트).</summary>
    public const int SequencePeriod = 255;

    /// <summary>제자리에서 XOR 한다. 보낼 때와 받을 때 같은 함수를 쓴다.</summary>
    public static void Apply(Span<byte> data, PseudorandomSequence sequence)
    {
        if (sequence == PseudorandomSequence.Legacy255)
        {
            Apply(data);
            return;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(data.Length, Standard131071MaxBytes);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= Standard131071Table[i];
        }
    }

    /// <summary>수열 자체를 얻는다 (시험·진단용).</summary>
    public static byte[] Sequence(int length, PseudorandomSequence sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var buffer = new byte[length];
        Apply(buffer, sequence);
        return buffer;
    }

    /// <summary>255 비트 수열(옛 호환용, <see cref="PseudorandomSequence.Legacy255"/>)로 제자리에서 XOR 한다.</summary>
    public static void Apply(Span<byte> data)
    {
        byte state = 0xFF;
        for (int i = 0; i < data.Length; i++)
        {
            byte mask = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                mask = (byte)((mask << 1) | (byte)(state >> 7));
                bool feedback = (System.Numerics.BitOperations.PopCount((uint)(state & FeedbackTaps)) & 1) == 1;
                state = (byte)((state << 1) | (feedback ? 1 : 0));
            }

            data[i] ^= mask;
        }
    }

    /// <summary>255 비트 수열(옛 호환용) 자체를 얻는다 (시험·진단용).</summary>
    public static byte[] Sequence(int length) => Sequence(length, PseudorandomSequence.Legacy255);

    private static byte[] BuildStandard131071Table()
    {
        var table = new byte[Standard131071MaxBytes];
        int register = Standard131071Seed;   // 비트 k = s[n+k]
        for (int i = 0; i < table.Length; i++)
        {
            int value = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value << 1) | (register & 1);
                int next = ((register >> 14) ^ register) & 1;
                register = (register >> 1) | (next << 16);
            }

            table[i] = (byte)value;
        }

        return table;
    }
}
