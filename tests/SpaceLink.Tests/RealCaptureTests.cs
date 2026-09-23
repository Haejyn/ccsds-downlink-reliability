using SpaceLink.ChannelCoding;

namespace SpaceLink.Tests;

/// <summary>
/// 실제 위성의 비트 — Astrocast 0.1 (NORAD 43798) 이 437.150 MHz 9k6 FSK 로 보낸 전파를 DK3WN 이 녹음한 것을
/// <c>tools/gen_golden_capture.py</c> 가 복조한 비트열이다(3.6 초, 34,856 비트). 표준 문서(§9.7)도 공개 구현(§9.10)도
/// 결국 "누군가 읽고 옮긴 것" 이었다 — 이 입력은 **실제 송신기의 출력**이라 ASM · PN · RS 의 이중 기저·인터리빙 · 프레임 CRC 의
/// 비트 배치를 한 번에 확인한다. 프레이밍은 gr-satellites 의 정의를 따른다: 프레임 1115 B, 인터리빙 5, 이중 기저, PN 255 비트.
/// </summary>
public class RealCaptureTests
{
    private const int FrameLength = 1115;
    private const int Depth = 5;

    private static readonly byte[] Capture = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden", "astrocast_9k6_bits.bin"));

    private static readonly FrameConfig AstrocastFrame = new(FrameLength, hasOperationalControlField: true, hasFrameErrorControl: true);

    private static List<byte[]> Codeblocks(ChannelCodec codec) => new FrameSynchronizer(codec.CodeblockLength).Process(Capture);

    [Fact]
    [Trait("Requirement", "REQ-CAP-01")]
    public void Real_satellite_codeblocks_decode_through_the_whole_receive_chain()
    {
        var codec = new ChannelCodec(FrameLength, Depth, sequence: PseudorandomSequence.Legacy255);
        List<byte[]> codeblocks = Codeblocks(codec);
        Assert.Equal(3, codeblocks.Count);   // 녹음에 든 CADU 전부

        var masterCounts = new List<int>();
        foreach (byte[] codeblock in codeblocks)
        {
            ChannelDecodeResult channel = codec.DecodeCodeblock(codeblock);
            Assert.True(channel.Succeeded, "실제 코드블록을 RS 가 복호하지 못했다");
            Assert.Equal(0, channel.CorrectedSymbols);   // 신호가 깨끗해 오류가 없다 — 정정 경로는 아래 시험이 본다

            // RS 는 부호어가 맞는지만 안다. 프레임 끝의 CRC-16 은 송신기가 따로 계산한 것이라, 이것이 맞으면
            // 되살린 223·5 바이트의 순서(인터리빙을 푸는 방향)까지 송신기와 같다는 뜻이다.
            FrameDecodeResult frame = TransferFrame.Decode(channel.TransferFrame, AstrocastFrame);
            Assert.True(frame.IsValid, $"전송 프레임 검증 실패: {frame.Error}");
            Assert.Equal(1, frame.Frame!.SpacecraftId);
            Assert.Equal(4, frame.Frame.VirtualChannelId);
            Assert.Equal(FirstHeaderPointer.IdleData, frame.Frame.FirstHeaderPointerValue);
            masterCounts.Add(frame.Frame.MasterChannelFrameCount);
        }

        Assert.Equal([0x17, 0x18, 0x19], masterCounts);   // 연속한 세 장
    }

    [Theory]
    [Trait("Requirement", "REQ-CAP-01")]
    [InlineData(PseudorandomSequence.Standard131071)]
    public void The_same_capture_does_not_decode_with_the_wrong_pseudorandom_sequence(PseudorandomSequence wrong)
    {
        // 위 시험이 "무엇이든 통과시키는 시험" 이 아님을 보인다 — 수열 하나만 바꿔도 세 장 모두 실패해야 한다.
        var codec = new ChannelCodec(FrameLength, Depth, sequence: wrong);
        Assert.All(Codeblocks(codec), codeblock => Assert.False(codec.DecodeCodeblock(codeblock).Succeeded));
    }

    [Fact]
    [Trait("Requirement", "REQ-CAP-01")]
    public void A_real_codeblock_is_restored_from_sixteen_symbol_errors_in_every_lane_and_refused_at_seventeen()
    {
        // 녹음은 오류가 없어 정정 경로를 한 번도 타지 않는다. 실제 코드블록 위에 레인마다 오류를 넣어 그 경로를 실제 데이터로 돈다.
        // 인터리빙 5 에서 레인 j 의 심볼은 코드블록의 j, j+5, j+10, … 자리다.
        var codec = new ChannelCodec(FrameLength, Depth, sequence: PseudorandomSequence.Legacy255);
        byte[] clean = Codeblocks(codec)[0];
        byte[] expected = codec.DecodeCodeblock(clean).TransferFrame!;
        var rng = new Random(9677);

        byte[] damaged = Corrupt(clean, rng, perLane: 16);
        ChannelDecodeResult restored = codec.DecodeCodeblock(damaged);
        Assert.True(restored.Succeeded);
        Assert.Equal(16 * Depth, restored.CorrectedSymbols);
        Assert.Equal(expected, restored.TransferFrame);

        byte[] tooMany = Corrupt(clean, rng, perLane: 17);
        Assert.False(codec.DecodeCodeblock(tooMany).Succeeded);
    }

    private static byte[] Corrupt(byte[] codeblock, Random rng, int perLane)
    {
        byte[] copy = (byte[])codeblock.Clone();
        for (int lane = 0; lane < Depth; lane++)
        {
            foreach (int symbol in Enumerable.Range(0, ReedSolomonCodec.SymbolsPerCodeword).OrderBy(_ => rng.Next()).Take(perLane))
            {
                copy[lane + (symbol * Depth)] ^= (byte)rng.Next(1, 256);
            }
        }

        return copy;
    }
}
