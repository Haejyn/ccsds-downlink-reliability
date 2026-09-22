namespace SpaceLink.Tests;

/// <summary>
/// A4 — CCSDS 131.0-B-5 표준 문서(공개 블루북, 저장소 밖 `~/Workspace/test-data/ccsds-standards/`)를
/// 받아 부호 생성 다항식·이중 기저 변환을 **값으로** 대조한다. 그전까지는 "체 생성 다항식과 정정 능력만
/// 같고 실제 CCSDS 비트열과는 호환되지 않는다" 고 적어 왔다(§9 도입부).
/// </summary>
public class StandardConformanceTests
{
    /// <summary>
    /// CCSDS 131.0-B-5 부속서 G "EXPANSION OF REED-SOLOMON COEFFICIENTS, For E = 16" 표 그대로 —
    /// 계수 Gi 를 α 의 지수로 싣는다(예: G1 = G31 = α^249). G0 = G32 = α^0 = 1.
    /// 표의 "NOTE — G3 = G29 = G13 = G19" 는 우연히 지수가 같다는 뜻이지 오탈자가 아니다 — 아래
    /// 배열에도 그대로 나타난다(인덱스 3·13·19·29 가 전부 66).
    /// </summary>
    private static readonly int[] AnnexGExponents =
    [
        0, 249, 59, 66, 4, 43, 126, 251, 97, 30, 3, 213, 50, 66, 170, 5, 24,
        5, 170, 66, 50, 213, 3, 30, 97, 251, 126, 43, 4, 66, 59, 249, 0,
    ];

    [Fact]
    [Trait("Requirement", "REQ-RS-01")]
    public void Generator_polynomial_matches_the_exact_coefficients_ccsds_publishes()
    {
        // 부속서 G 는 지수 33 개(G0…G32)를 싣는다. 여기서는 그 지수들로 α^exponent 를 계산해
        // "생성 다항식이 표준과 같은가" 를 구현이 아니라 문서가 실은 값으로 직접 확인한다.
        Assert.Equal(ChannelCoding.ReedSolomonCodec.ParitySymbolsPerCodeword + 1, AnnexGExponents.Length);

        byte[] expected = new byte[AnnexGExponents.Length];
        for (int i = 0; i < AnnexGExponents.Length; i++)
        {
            expected[i] = ChannelCoding.GaloisField256.Exp(AnnexGExponents[i]);
        }

        // Generator 는 private static 이라 시험이 접근할 방법이 없다 — 대신 unit 벡터를 부호화해
        // 그 자체가 곧 생성 다항식의 계수임을 이용한다 (관례 기저에서, EncodeCodeword 의 원리다).
        // Encode 는 정보 심볼을 이중 기저로 받으므로, 1(관례) 을 넣으려면 이중 기저로 바꿔 넣어야 한다.
        var codec = new ChannelCoding.ReedSolomonCodec();
        var unit = new byte[codec.DataLength];
        unit[^1] = ChannelCoding.DualBasisTransform.ToDualBasis(1);
        byte[] unitCodeword = codec.Encode(unit);

        const int lastIndex = ChannelCoding.ReedSolomonCodec.SymbolsPerCodeword - 1;
        for (int degree = 0; degree < expected.Length; degree++)
        {
            byte actualConventional = ChannelCoding.DualBasisTransform.ToConventional(unitCodeword[lastIndex - degree]);
            Assert.True(expected[degree] == actualConventional,
                $"G{degree} 가 부속서 G 표(지수 {AnnexGExponents[degree]}, 값 {expected[degree]:X2})와 다르다 (실제 {actualConventional:X2})");
        }
    }

    /// <summary>
    /// 부속서 F 의 T_λ(관례→이중) 행렬 — 표준 문서 §4.3.9.3 · 부속서 F 두 곳에 실린 값이 일치했다.
    /// 첫·마지막 행(α^7·α^0)의 이중 기저 값은 문서가 값으로 직접 싣는 예시다.
    /// </summary>
    [Theory]
    [Trait("Requirement", "REQ-RS-01")]
    [InlineData(0b1000_0000, 0b1000_1101)] // alpha^7 -> Tlambda 첫 행
    [InlineData(0b0000_0001, 0b0111_1011)] // alpha^0 -> Tlambda 마지막 행
    public void Dual_basis_of_known_field_elements_matches_the_standards_matrix(byte conventional, byte expectedDual)
    {
        Assert.Equal(expectedDual, ChannelCoding.DualBasisTransform.ToDualBasis(conventional));
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-01")]
    public void Dual_basis_transform_round_trips_for_every_byte_value()
    {
        for (int value = 0; value < 256; value++)
        {
            byte v = (byte)value;
            byte dual = ChannelCoding.DualBasisTransform.ToDualBasis(v);
            byte back = ChannelCoding.DualBasisTransform.ToConventional(dual);
            Assert.True(v == back, $"{v:X2} -> 이중 기저 {dual:X2} -> 관례 기저 {back:X2}, 왕복하지 않는다");
        }

        // 항등 변환이 아님을 함께 확인한다 — 왕복만 되고 값이 안 바뀌면 이 클래스가 아무 일도 안 하는 것이다.
        Assert.NotEqual((byte)1, ChannelCoding.DualBasisTransform.ToDualBasis(1));
    }
}
