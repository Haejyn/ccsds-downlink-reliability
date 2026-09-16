namespace SpaceLink.Tests;

/// <summary>
/// RS 복호기가 **정정 능력을 넘으면 실패를 선언하는가**. 무작위 오류로는 사실상 만들어지지 않는
/// 신드롬 모양을 체 이론으로 직접 구성해 그 경로만 찌른다.
///
/// 이 두 시험은 뮤테이션 루프(`tools/mutation_loop/`)가 생성한 초안을 사람이 검토해 남긴 것이다.
/// 넣기 전 생존 뮤턴트 10 개 → 넣은 뒤 7 개. 죽은 셋은 전부 REQ-RS-03 의 핵심이었다:
///   · `errorCount == 0 || errorCount > CorrectableSymbols` 의 `||` → `&&`
///   · 그 분기의 `return false` → `true`   (실패 선언 자체)
///   · Berlekamp-Massey 작업 공간 `ParitySymbolsPerCodeword + 1` → `- 1`
/// 이전 시험(`Beyond_correction_capability_…`)은 "실패 선언이든 오정정이든" 을 모두 받아들여서
/// 이 셋을 잡지 못했다 — 3,000 건이 전부 실패로 나왔다는 사실과, 그 판정이 *이 분기에서* 나왔다는
/// 보장은 다른 이야기다.
/// </summary>
public class ReedSolomonFailurePathTests
{
    [Fact]
    [Trait("Requirement", "REQ-RS-03")]
    public void Seventeen_errors_spaced_fifteen_symbols_apart_are_reported_as_failure()
    {
        // 255 = 15·17 이라, 15 칸 간격 오류 17 개는 신드롬 32 개 중 S17 하나만 남긴다.
        // 그러면 Berlekamp-Massey 가 Λ(x) = 1 + s·x^17 을 내는데, 이 s 에서는 근이 체 안에 17 개 전부 있다.
        // 오류 개수가 정정 능력을 넘었다고 **먼저 거절하지 않으면** 근 17 개가 위치 16 칸에 넘친다.
        var codec = new ChannelCoding.ReedSolomonCodec();
        var data = new byte[codec.DataLength];
        new Random(1715).NextBytes(data);
        byte[] codeblock = codec.Encode(data);
        for (int position = 0; position < ChannelCoding.ReedSolomonCodec.SymbolsPerCodeword; position += 15)
        {
            codeblock[position] ^= 0x01;
        }

        var decoded = new byte[codec.DataLength];
        ChannelCoding.ReedSolomonResult result = codec.Decode(codeblock, decoded);

        Assert.False(result.Succeeded, "심볼 오류 17 개는 t = 16 을 넘으므로 실패로 보고해야 한다");
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-03")]
    public void Errors_that_vanish_the_first_thirty_syndromes_are_reported_as_failure()
    {
        // 오류 e(x) = Π_{j=1..30} (x − α^j) 는 S1 … S30 을 0 으로, S31 만 0 이 아니게 만든다.
        // Berlekamp-Massey 는 끝에서 두 번째 단계에서 차수를 31 로 올리고 마지막 단계에서 그 계수를 읽는다 —
        // 위치 다항식 버퍼가 패리티 수 + 1 만큼 크지 않으면 여기서 예외로 죽는다.
        // 무작위 오류로 이 모양이 나올 확률은 2^-240 수준이라 직접 만든다.
        const int lastIndex = ChannelCoding.ReedSolomonCodec.SymbolsPerCodeword - 1;
        var codec = new ChannelCoding.ReedSolomonCodec();
        var powers = new byte[ChannelCoding.ReedSolomonCodec.ParitySymbolsPerCodeword + 1];
        powers[0] = 1;
        for (int j = 1; j < powers.Length; j++)
        {
            powers[j] = Multiply(powers[j - 1], 2);
        }

        byte[] pattern = [1];
        for (int j = 1; j <= 30; j++)
        {
            pattern = TimesLinear(pattern, powers[j]);
        }

        // 시험 쪽 체 산술이 부호기와 같은 체인지 먼저 못 박는다 — 다르면 아래 오류 모양이 뜻을 잃고 시험이 헛돈다.
        // 마지막 데이터 심볼 하나만 1 이면 패리티 자리에 g(x) = e(x)·(x − α^31)·(x − α^32) 의 계수가 그대로 나온다.
        byte[] generator = TimesLinear(TimesLinear(pattern, powers[31]), powers[32]);
        var unit = new byte[codec.DataLength];
        unit[^1] = 1;
        byte[] unitCodeword = codec.Encode(unit);
        for (int degree = 0; degree < ChannelCoding.ReedSolomonCodec.ParitySymbolsPerCodeword; degree++)
        {
            Assert.Equal(generator[degree], unitCodeword[lastIndex - degree]);
        }

        var data = new byte[codec.DataLength];
        new Random(3031).NextBytes(data);
        byte[] codeblock = codec.Encode(data);
        for (int degree = 0; degree < pattern.Length; degree++)
        {
            codeblock[lastIndex - degree] ^= pattern[degree];   // 인덱스 0 이 x^254 다
        }

        var decoded = new byte[codec.DataLength];
        ChannelCoding.ReedSolomonResult result = codec.Decode(codeblock, decoded);

        Assert.False(result.Succeeded, "t = 16 을 넘는 오류는 실패로 보고해야 한다");
    }

    /// <summary>GF(256) 곱셈 — 기약다항식 x^8 + x^7 + x^2 + x + 1 (0x187). 구현과 독립으로 시험 안에서 계산한다.</summary>
    private static byte Multiply(byte a, byte b)
    {
        int product = 0;
        int shifted = a;
        for (; b != 0; b >>= 1)
        {
            if ((b & 1) != 0)
            {
                product ^= shifted;
            }

            shifted <<= 1;
            if ((shifted & 0x100) != 0)
            {
                shifted ^= 0x187;
            }
        }

        return (byte)product;
    }

    /// <summary>poly·(x + root). 계수는 낮은 차수부터.</summary>
    private static byte[] TimesLinear(byte[] poly, byte root)
    {
        var next = new byte[poly.Length + 1];
        for (int i = 0; i < poly.Length; i++)
        {
            next[i + 1] ^= poly[i];
            next[i] ^= Multiply(poly[i], root);
        }

        return next;
    }
}
