namespace SpaceLink.ChannelCoding;

/// <summary>
/// CCSDS 131.0-B-5 §4.3.9 · 부속서 F — RS 부호 계산(신드롬·Berlekamp-Massey·Chien·Forney)은
/// "관례(conventional)" 기저 α^7…α^0 로 하고, 실제로 내보내는 심볼 바이트는 "이중(dual, Berlekamp)"
/// 기저 z0…z7 다. 이 변환이 없으면 부호화·복호 절차는 맞아도 전송되는 8비트 값이 표준과 다르다 —
/// RS 계층이 CCSDS 의 코드가 아니게 되는 이유의 절반이다(나머지 절반은 부호 생성 다항식의 근,
/// <see cref="ReedSolomonCodec"/> 참조).
///
/// 8×8 GF(2) 행렬 T_λ(관례→이중, 부속서 F 의 "T_λ")와 그 역행렬(이중→관례, "T_λ⁻¹")을 그대로 옮겼다.
/// 같은 행렬이 표준 문서에 §4.3.9.3 과 부속서 F 두 곳에 다른 표로 실려 있는데 바이트 단위로 일치했고,
/// 아래를 독립적으로 확인했다 (구현이 아니라 문서 자체에서, Python 으로 계산 — 결과만 이 주석에 남긴다):
///   1) T_λ · T_λ⁻¹ = 항등 — 256 개 값 전부 왕복한다 (아래 시험이 그대로 확인한다).
///   2) §4.4.2 의 이중 기저 정의 Tr(λᵢ·βʲ) = δᵢⱼ (β = α^117 — 부속서 F 의 RS 근 간격 α^11 과는
///      **별개의** 예시 기저다, pdftotext 가 "117" 을 "11" 로 잘못 잘라 처음엔 착각했다) 를
///      T_λ⁻¹ 에서 뽑은 λᵢ 로 직접 계산해 만족함을 확인했다.
///
/// "z0 이 먼저 전송된다"(§4.3.9.2) 는 **비트** 전송 순서 요구를 바이트의 MSB 가 z0 인 값으로 옮겼다 — 공개 구현 libfec 의
/// 변환표(Taltab)와 같은 관례이고, 부호화 결과가 바이트 단위로 같다(<c>LibfecCrossCheckTests</c>).
/// ⚠ 실제 위성에서 캡처한 비트열과는 대조하지 못했다.
/// </summary>
internal static class DualBasisTransform
{
    /// <summary>
    /// T_λ 행 i (0-index) = α^(7-i) 의 이중 기저 표현 [z0..z7] (문서 §4.3.9.3, 부속서 F. z0 이 MSB).
    /// </summary>
    private static readonly byte[] ToDualBasisRows =
    [
        0b1000_1101, // alpha^7
        0b1110_1111, // alpha^6
        0b1110_1100, // alpha^5
        0b1000_0110, // alpha^4
        0b1111_1010, // alpha^3
        0b1001_1001, // alpha^2
        0b1010_1111, // alpha^1
        0b0111_1011, // alpha^0
    ];

    /// <summary>
    /// T_λ⁻¹ 행 i (0-index) = l_i 의 관례 기저 표현 [α^7 계수 .. α^0 계수] (부속서 F).
    /// 이 값 자체가 λᵢ 를 보통의 바이트(비트 7=α^7 계수) 로 나타낸 것과 같다.
    /// </summary>
    private static readonly byte[] ToConventionalRows =
    [
        0b1100_0101, // l0
        0b0100_0010, // l1
        0b0010_1110, // l2
        0b1111_1101, // l3
        0b1111_0000, // l4
        0b0111_1001, // l5
        0b1010_1100, // l6
        0b1100_1100, // l7
    ];

    private static readonly byte[] DualBasisLookup = BuildLookup(ToDualBasisRows);
    private static readonly byte[] ConventionalLookup = BuildLookup(ToConventionalRows);

    /// <summary>수신한 심볼(이중 기저)을 부호 계산용 관례 기저로 바꾼다.</summary>
    public static byte ToConventional(byte dualBasisValue) => ConventionalLookup[dualBasisValue];

    /// <summary>계산한 심볼(관례 기저)을 전송용 이중 기저로 바꾼다.</summary>
    public static byte ToDualBasis(byte conventionalValue) => DualBasisLookup[conventionalValue];

    /// <summary>
    /// 두 방향 모두 같은 구조다 — 행 i 는 입력 바이트의 비트 (7−i)(MSB 부터) 가 서 있을 때만 결과에 XOR 된다.
    /// 각 행 자체가 이미 [MSB..LSB] = [출력의 최상위 비트..최하위 비트] 순서로 채워져 있어 그대로 XOR 하면 된다.
    /// </summary>
    private static byte[] BuildLookup(byte[] rows)
    {
        var table = new byte[256];
        for (int input = 0; input < 256; input++)
        {
            byte output = 0;
            for (int row = 0; row < 8; row++)
            {
                if (((input >> (7 - row)) & 1) != 0)
                {
                    output ^= rows[row];
                }
            }

            table[input] = output;
        }

        return table;
    }
}
