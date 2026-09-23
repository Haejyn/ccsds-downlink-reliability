using SpaceLink.ChannelCoding;

namespace SpaceLink.Tests;

/// <summary>
/// 외부 구현과의 대조 — libfec(Phil Karn)의 CCSDS RS(255,223) 가 만든 기준 벡터와 바이트 단위로 맞춘다.
/// 부속서 G 계수표(§9.7)·가상 채움의 정의(§9.8)는 **표준 문서**와의 대조였다. 여기는 지상국 소프트웨어에서 널리 쓰는
/// 공개 구현의 **실제 출력**과의 대조다 — 생성 다항식·이중 기저·바이트 안의 비트 배치·짧은 코드블록을 한 번에 확인한다.
/// 벡터는 <c>tools/gen_golden_libfec.py</c> 가 libfec 고정 커밋을 빌드해 만들고, CI 가 다시 만들어 저장소 파일과 대조한다.
/// </summary>
public class LibfecCrossCheckTests
{
    private sealed record EncodeVector(string Kind, int Pad, byte[] Data, byte[] Parity);

    private sealed record DecodeVector(string Kind, int Pad, byte[] Received, int Result, byte[] Output);

    private static readonly string[] Lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "golden", "libfec_rs_vectors.txt"))
        .Where(line => line.Length > 0 && line[0] != '#')
        .ToArray();

    private static IEnumerable<EncodeVector> EncodeVectors() =>
        Lines.Where(l => l.StartsWith("E ", StringComparison.Ordinal)).Select(l => l.Split(' ')).Select(p =>
            new EncodeVector(p[1], int.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture), Convert.FromHexString(p[3]), Convert.FromHexString(p[4])));

    private static IEnumerable<DecodeVector> DecodeVectors() =>
        Lines.Where(l => l.StartsWith("D ", StringComparison.Ordinal)).Select(l => l.Split(' ')).Select(p =>
            new DecodeVector(p[1], int.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture), Convert.FromHexString(p[3]),
                int.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture), Convert.FromHexString(p[5])));

    [Fact]
    [Trait("Requirement", "REQ-RS-06")]
    public void Encoder_matches_libfec_byte_for_byte_including_shortened_codeblocks()
    {
        var vectors = EncodeVectors().ToList();
        Assert.Equal(35, vectors.Count);   // 가상 채움 다섯 가지 × 입력 일곱 가지

        foreach (EncodeVector v in vectors)
        {
            byte[] codeblock = new ReedSolomonCodec(1, v.Pad).Encode(v.Data);
            Assert.True(codeblock.AsSpan(0, v.Data.Length).SequenceEqual(v.Data), $"{v.Kind} pad={v.Pad}: 정보 심볼이 바뀌었다");
            Assert.True(codeblock.AsSpan(v.Data.Length).SequenceEqual(v.Parity),
                $"{v.Kind} pad={v.Pad}: 패리티가 libfec 와 다르다 (우리 {Convert.ToHexString(codeblock.AsSpan(v.Data.Length))[..16]}… · libfec {Convert.ToHexString(v.Parity)[..16]}…)");
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-06")]
    public void Decoder_agrees_with_libfec_on_correction_count_output_and_failure()
    {
        // 두 복호기 모두 "16 심볼 안쪽에 부호어가 하나 있으면 그것으로, 없으면 실패" 다 — 수학적으로 답이 하나라 판정이 같아야 한다.
        var vectors = DecodeVectors().Where(v => v.Kind != "fill-correction").ToList();
        Assert.Equal(25, vectors.Count);   // 가상 채움 다섯 가지 × 오류 0·1·8·16·17
        Assert.Contains(vectors, v => v.Result == -1);   // 정정 능력 밖(17 개)도 들어 있다

        foreach (DecodeVector v in vectors)
        {
            var codec = new ReedSolomonCodec(1, v.Pad);
            var data = new byte[codec.DataLength];
            ReedSolomonResult result = codec.Decode(v.Received, data);
            if (v.Result < 0)
            {
                Assert.False(result.Succeeded, $"{v.Kind} pad={v.Pad}: libfec 는 실패로 알렸는데 이 구현은 성공이라 했다");
                continue;
            }

            Assert.True(result.Succeeded, $"{v.Kind} pad={v.Pad}: libfec 는 {v.Result} 개를 정정했는데 이 구현은 실패했다");
            Assert.Equal(v.Result, result.CorrectedSymbols);
            Assert.True(data.AsSpan().SequenceEqual(v.Output.AsSpan(0, data.Length)), $"{v.Kind} pad={v.Pad}: 복호한 데이터가 libfec 와 다르다");
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-06")]
    public void On_a_correction_that_lands_in_the_fill_libfec_passes_damaged_data_and_this_decoder_refuses()
    {
        // libfec 의 decode_rs.h 는 오류 위치가 채움 자리(loc < pad)면 그 정정만 건너뛰고 정정 수를 그대로 돌려준다 — 실패로 알리지 않는다.
        // 이 수신어는 채움을 0 으로 되살리면 채움 자리 하나만 다른 부호어로 간다(ShortenedCodeblockTests 와 같은 구성).
        // 원래 프레임은 같은 부호어를 오류 없이 복호한 벡터(errors-0, pad 95)의 정보 심볼이다.
        DecodeVector fill = DecodeVectors().Single(v => v.Kind == "fill-correction");
        DecodeVector clean = DecodeVectors().Single(v => v.Kind == "errors-0" && v.Pad == fill.Pad);
        int dataLength = ReedSolomonCodec.DataSymbolsPerCodeword - fill.Pad;
        byte[] original = clean.Received[..dataLength];

        Assert.True(fill.Result > 0, "기준 벡터에서 libfec 는 이 수신어를 성공으로 알렸다");
        Assert.False(fill.Output.AsSpan(0, dataLength).SequenceEqual(original),
            "libfec 가 성공이라 하며 내보낸 데이터는 원래 프레임과 다르다 — 손상 데이터다");

        var codec = new ReedSolomonCodec(1, fill.Pad);
        ReedSolomonResult result = codec.Decode(fill.Received, new byte[codec.DataLength]);
        Assert.False(result.Succeeded, "이 구현은 채움 자리로 가는 정정을 실패로 알려야 한다 (REQ-RS-05)");
    }
}
