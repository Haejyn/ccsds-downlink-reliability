namespace SpaceLink.ChannelCoding;

/// <summary>
/// 의사잡음(PN) 랜덤화 — 같은 비트가 길게 이어지면 수신기가 비트 동기를 잃으므로, 보내기 전에 고정 수열과 XOR 한다.
/// LFSR 다항식 h(x) = x^8 + x^7 + x^5 + x^3 + 1, 초기 상태는 전부 1 (CCSDS 131.0-B 계열).
///
/// ⚠ 한계: 표준 문서에 실린 예시 수열과 대조하지는 못했다(문서 미보유). 그래서 시험은 수열 값이 아니라
/// **성질**을 고정한다 — 두 번 적용하면 원본으로 돌아온다(자기 역원), 수열 주기는 255 바이트, 첫 바이트는 0xFF.
/// </summary>
public static class Pseudorandomizer
{
    /// <summary>
    /// LFSR 되먹임 탭. MSB 로 내보내며 왼쪽으로 미는 구현이라 h(x) 의 항 x^7 · x^5 · x^3 · 1 은
    /// 비트 7 · 6 · 4 · 2 자리에 놓인다 (0xD4).
    ///
    /// ⚠ 지수를 비트 번호로 그대로 옮기면(0xA9) 다른 다항식이 되고, 실측 주기가 255 가 아니라
    /// **217** 로 떨어진다 — 같은 패턴이 더 자주 되풀이되니 랜덤화기 구실을 못 한다.
    /// 처음에 그렇게 썼다가 주기 시험이 잡았다.
    /// </summary>
    public const int FeedbackTaps = 0b1101_0100;

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
