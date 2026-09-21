namespace SpaceLink.ChannelCoding;

/// <summary>
/// 의사잡음(PN) 랜덤화 — 같은 비트가 길게 이어지면 수신기가 비트 동기를 잃으므로, 보내기 전에 고정 수열과 XOR 한다.
/// LFSR 다항식 h(x) = x^8 + x^7 + x^5 + x^3 + 1, 초기 상태는 전부 1 (CCSDS 131.0-B 계열).
///
/// 표준(CCSDS 131.0-B-5 §10.4.3 NOTE 2)이 싣는 처음 40 비트 <c>FF 48 0E C0 9A</c> 와 시험이 값으로 대조한다.
///
/// ⚠ 한계: 표준이 기본으로 정한 131071 비트 수열(h(x) = x^17 + x^14 + 1, §10.4.1)은 구현하지 않았다.
/// 이 255 비트 수열은 옛 시스템과의 호환용이다(§10.4.2) — 표준은 1/255 심볼율에 스펙트럼 선이 생길 수 있다고 경고한다.
/// </summary>
public static class Pseudorandomizer
{
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

    /// <summary>수열 자체를 얻는다 (시험·진단용).</summary>
    public static byte[] Sequence(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var buffer = new byte[length];
        Apply(buffer);
        return buffer;
    }
}
