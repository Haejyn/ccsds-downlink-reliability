namespace SpaceLink.Tests;

/// <summary>
/// 짧은 코드블록 — 가상 채움(virtual fill, CCSDS 131.0-B-5 §4.3.7). 전송 프레임이 223·I 보다 짧으면
/// 코드블록 앞쪽 Q 심볼을 0 으로 치고 부호화하되 그 0 은 보내지 않는다.
///
/// 예전 구현은 프레임을 앞에 두고 **뒤를** 0 으로 채워 255 바이트를 **전부** 보냈다 — 128 바이트 프레임에
/// CADU 259 바이트(표준은 164), 채움 자리가 달라 패리티도 표준 부호기와 달랐다. 이 저장소의 프레임은 전부
/// 128 바이트라, A4(#6)에서 "RS 는 표준의 코드" 라고 적은 것은 223 바이트 프레임에서만 맞았다.
/// </summary>
public class ShortenedCodeblockTests
{
    private static ChannelCoding.ReedSolomonCodec Full(int depth) => new(depth);

    private static ChannelCoding.ReedSolomonCodec Shortened(int depth, int frameLength) =>
        new(depth, (ChannelCoding.ReedSolomonCodec.DataSymbolsPerCodeword * depth) - frameLength);

    private static byte[] RandomFrame(int length, int seed)
    {
        var frame = new byte[length];
        new Random(seed).NextBytes(frame);
        return frame;
    }

    [Theory]
    [Trait("Requirement", "REQ-RS-05")]
    [InlineData(1, 128)]
    [InlineData(1, 1)]
    [InlineData(4, 128)]
    [InlineData(5, 125)]
    public void Shortened_codeblock_is_the_full_codeword_with_the_leading_fill_removed(int depth, int frameLength)
    {
        // 기대값은 표준의 정의 그대로다(§4.3.7.3·4): 체크 심볼은 앞 Q 심볼을 0 으로 친 kI 심볼 위에서 계산하고,
        // 그 앞 Q 개는 보내지 않는다. 그러니 "앞에 0 을 Q 개 붙여 전체 길이로 부호화한 뒤 그 Q 개를 뗀 것" 이 정답이다.
        // 전체 길이 부호기는 부속서 G 계수표와 값으로 대조돼 있다(StandardConformanceTests) — 그 위에 올라탄 대조다.
        byte[] frame = RandomFrame(frameLength, 4370 + frameLength);
        var fullCodec = Full(depth);
        var shortCodec = Shortened(depth, frameLength);
        int fill = shortCodec.VirtualFill;

        var padded = new byte[fullCodec.DataLength];
        frame.CopyTo(padded.AsSpan(fill));
        byte[] fullCodeblock = fullCodec.Encode(padded);
        byte[] shortCodeblock = shortCodec.Encode(frame);

        Assert.True(fullCodeblock.AsSpan(0, fill).IndexOfAnyExcept((byte)0) < 0, "전체 부호어의 앞 Q 심볼은 0 이어야 한다(조직 부호)");
        Assert.True(fullCodeblock.AsSpan(fill).SequenceEqual(shortCodeblock),
            $"짧은 코드블록이 '앞 0 {fill} 개를 뗀 전체 부호어' 와 다르다 (I={depth}, 프레임 {frameLength} B)");
        Assert.Equal(frameLength + (ChannelCoding.ReedSolomonCodec.ParitySymbolsPerCodeword * depth), shortCodeblock.Length);
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-05")]
    public void Cadu_carries_only_the_frame_and_the_check_symbols()
    {
        // 128 바이트 프레임: ASM 4 + 프레임 128 + 체크 32 = 164. 예전에는 채움 95 바이트까지 보내 259 였다.
        Assert.Equal(164, new ChannelCoding.ChannelCodec(128).CaduLength);
        Assert.Equal(259, new ChannelCoding.ChannelCodec(223).CaduLength);          // 채움 없음 — 최대 길이
        Assert.Equal(4 + 128 + (32 * 4), new ChannelCoding.ChannelCodec(128, interleavingDepth: 4).CaduLength);
    }

    [Theory]
    [Trait("Requirement", "REQ-RS-05")]
    [InlineData(1, 128)]
    [InlineData(4, 128)]
    public void Up_to_sixteen_errors_per_codeword_are_still_corrected_in_a_shortened_codeblock(int depth, int frameLength)
    {
        // 짧아져도 정정 능력은 부호어당 16 심볼 그대로다. 오류는 보낸 자리에만 생길 수 있다.
        var codec = Shortened(depth, frameLength);
        byte[] frame = RandomFrame(frameLength, 1616);
        byte[] codeblock = codec.Encode(frame);

        var rnd = new Random(2016);
        int perLaneTransmitted = codeblock.Length / depth;
        for (int lane = 0; lane < depth; lane++)
        {
            foreach (int symbol in Enumerable.Range(0, perLaneTransmitted).OrderBy(_ => rnd.Next()).Take(16))
            {
                codeblock[(symbol * depth) + lane] ^= (byte)rnd.Next(1, 256);
            }
        }

        var decoded = new byte[codec.DataLength];
        ChannelCoding.ReedSolomonResult result = codec.Decode(codeblock, decoded);

        Assert.True(result.Succeeded, "부호어당 16 개는 정정해야 한다");
        Assert.Equal(16 * depth, result.CorrectedSymbols);
        Assert.Equal(frame, decoded);
    }

    [Theory]
    [Trait("Requirement", "REQ-RS-05")]
    [InlineData(0)]
    [InlineData(94)]
    public void A_correction_that_lands_on_the_virtual_fill_is_reported_as_failure(int fillPosition)
    {
        // fillPosition — 채움의 첫 자리(0)와 마지막 자리(Q−1 = 94). 마지막 자리는 Chien 탐색이 보는 범위의 바로 바깥이라,
        // 탐색 범위를 한 칸 늘리는 실수(`<` → `<=`)를 이 자리만 잡는다 — 첫 자리만 시험할 때는 그 뮤턴트가 살아남았다(§8.6 후속).
        // 채움 자리는 0 인 게 확실하다 — 복호기가 그 자리를 "고쳐야" 한다고 결론 내면, 받은 것은 채움이 0 인
        // 어떤 부호어에서도 16 심볼 안쪽에 있지 않다는 뜻이다. 이를 무시하면 조용히 다른 데이터를 내보낸다.
        //
        // 구성: c1 = 원래 프레임의 부호어, c2 = 채움 자리(0)와 첫 데이터 자리(Q)에만 값이 있는 전체 길이 부호어.
        // 받은 것 = c1 ⊕ c2 에서 **보낸 자리만**. 복호기가 채움을 0 으로 되살리면 부호어 c1 ⊕ c2 와 채움 자리
        // 하나만 다르다(거리 1). 최소 거리가 33 이라 16 안쪽의 부호어는 c1 ⊕ c2 하나뿐이고, 그 채움은 0 이 아니다.
        // 채움 자리로 정정해 버리면 결과는 "프레임의 첫 바이트가 바뀐 채 성공" — 손상 데이터를 내보내는 것이다.
        const int frameLength = 128;
        var fullCodec = Full(1);
        var shortCodec = Shortened(1, frameLength);
        int fill = shortCodec.VirtualFill;

        byte[] frame = RandomFrame(frameLength, 9503);
        byte[] c1 = shortCodec.Encode(frame);

        var e = new byte[fullCodec.DataLength];
        e[fillPosition] = 0x5A;   // 채움 자리
        e[fill] = 0xC3;       // 보내는 첫 데이터 자리
        byte[] c2 = fullCodec.Encode(e);

        byte[] received = (byte[])c1.Clone();
        for (int i = 0; i < received.Length; i++)
        {
            received[i] ^= c2[fill + i];   // 이중 기저 표현끼리의 XOR = 부호어의 덧셈 (변환이 GF(2) 선형이다)
        }

        var decoded = new byte[shortCodec.DataLength];
        ChannelCoding.ReedSolomonResult result = shortCodec.Decode(received, decoded);

        Assert.False(result.Succeeded,
            $"정정 위치가 채움 자리로 나왔으면 실패로 알려야 한다 — 성공이라 하면 첫 바이트가 {decoded[0]:X2}(원래 {frame[0]:X2})인 프레임을 내보낸다");
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-05")]
    public void Fill_must_be_a_multiple_of_the_interleaving_depth()
    {
        // Q 는 I 의 배수, 0 이상, kI 미만 (§4.3.7.3).
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelCoding.ReedSolomonCodec(1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelCoding.ReedSolomonCodec(1, 223));
        Assert.Throws<ArgumentException>(() => new ChannelCoding.ReedSolomonCodec(4, 6));
        Assert.Equal(222, new ChannelCoding.ReedSolomonCodec(1, 222).VirtualFill);   // 데이터 1 심볼까지 줄일 수 있다

        // ChannelCodec 에서는 프레임 길이가 I 의 배수여야 한다는 뜻이 된다 — 126 B 를 깊이 4 로는 못 담는다(채움 766).
        var ex = Assert.Throws<ArgumentException>(() => new ChannelCoding.ChannelCodec(126, interleavingDepth: 4));
        Assert.Equal("transferFrameLength", ex.ParamName);
    }
}
