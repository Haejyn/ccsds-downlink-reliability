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

    // ── EIRSAT-1 — 잡음이 섞인 실제 링크 (SatNOGS 관측 12324560, EA5WA 지상국, CC BY-SA 4.0) ─────────────
    // 188 초 녹음에서 복조기가 찾은 CADU 46 장(경판정 비트 그대로)과, 같은 녹음을 SatNOGS 지상국이 자체 복호한 프레임 26 장.
    // 프레이밍(gr-satellites EIRSAT-1.yml): 프레임 892 B, 인터리빙 4, 이중 기저, PN 255 비트, FECF·OCF 없음.

    private const int EirsatFrameLength = 892;

    private static readonly FrameConfig EirsatFrame = new(EirsatFrameLength, hasOperationalControlField: false, hasFrameErrorControl: false);

    private static List<ChannelDecodeResult> DecodeEirsat()
    {
        var codec = new ChannelCodec(EirsatFrameLength, 4, sequence: PseudorandomSequence.Legacy255);
        byte[] stream = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden", "eirsat1_cadus.bin"));
        return new FrameSynchronizer(codec.CodeblockLength).Process(stream).Select(codeblock => codec.DecodeCodeblock(codeblock)).ToList();
    }

    private static HashSet<string> SatnogsFrames()
    {
        byte[] frames = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden", "eirsat1_satnogs_frames.bin"));
        return Enumerable.Range(0, frames.Length / EirsatFrameLength)
            .Select(i => Convert.ToHexString(frames, i * EirsatFrameLength, EirsatFrameLength)).ToHashSet();
    }

    [Fact]
    [Trait("Requirement", "REQ-CAP-02")]
    public void A_noisy_real_pass_is_corrected_and_agrees_with_an_independent_ground_station()
    {
        List<ChannelDecodeResult> results = DecodeEirsat();
        HashSet<string> satnogs = SatnogsFrames();
        List<ChannelDecodeResult> decoded = results.Where(r => r.Succeeded).ToList();

        Assert.Equal(46, results.Count);     // 복조기가 찾은 CADU 전부가 동기를 거친다
        Assert.Equal(31, decoded.Count);     // 나머지 15 장은 정정 능력(레인당 16 심볼)을 넘어 실패로 알린다
        Assert.Equal(26, satnogs.Count);

        // RS 가 실제로 일했다 — 이 링크는 깨끗하지 않다(Astrocast 녹음과 다르다).
        Assert.Equal(323, decoded.Sum(r => r.CorrectedSymbols));
        Assert.Contains(decoded, r => r.CorrectedSymbols > 16);

        // 다른 사람의 복조기·복호기(SatNOGS 지상국의 gr-satellites)가 같은 녹음에서 낸 프레임과 바이트 단위로 같다.
        int agreeing = decoded.Count(r => satnogs.Contains(Convert.ToHexString(r.TransferFrame!)));
        Assert.Equal(25, agreeing);          // 26 장 중 하나(마스터 카운트 199)는 이 저장소의 복조기가 ASM 을 못 찾았다
    }

    [Fact]
    [Trait("Requirement", "REQ-CAP-02")]
    public void Frames_the_ground_station_missed_fill_its_counter_gaps_and_carry_packets_across_them()
    {
        // SatNOGS 에 없는 6 장은 오정정일 수도 있다 — 그래서 두 가지로 확인한다.
        // (1) 마스터 프레임 카운트가 SatNOGS 목록의 **빈자리**에 정확히 들어가고 시간 순으로 늘어난다.
        // (2) 패킷이 그 프레임을 가로질러 이어진다 — 오정정된 프레임이면 제1 헤더 포인터가 조립 중인 패킷 경계와 어긋나거나
        //     패킷 헤더가 깨져 HeaderPointerMismatch · InvalidPacketHeader 가 나온다.
        List<byte[]> frames = DecodeEirsat().Where(r => r.Succeeded).Select(r => r.TransferFrame!).ToList();
        HashSet<string> satnogs = SatnogsFrames();
        var satnogsCounts = satnogs.Select(hex => Convert.FromHexString(hex)[2]).ToHashSet();

        byte[] counts = frames.Select(f => f[2]).ToArray();
        Assert.Equal(counts.OrderBy(c => c), counts);                       // 늘어나기만 한다(이 관측에서는 카운트가 돌지 않는다)
        byte[] extras = frames.Where(f => !satnogs.Contains(Convert.ToHexString(f))).Select(f => f[2]).ToArray();
        Assert.Equal([196, 224, 234, 248, 249, 250], extras);
        Assert.All(extras, c => Assert.DoesNotContain(c, satnogsCounts));

        var extractor = new PacketExtractor(EirsatFrame);
        var packets = new List<SpacePacket>();
        var events = new List<LinkEvent>();
        foreach (byte[] frame in frames)
        {
            ExtractionResult result = extractor.Process(frame);
            packets.AddRange(result.Packets);
            events.AddRange(result.Events);
        }

        Assert.Equal(88, packets.Count);
        Assert.All(packets, p => Assert.Equal(2, p.Apid));
        Assert.DoesNotContain(events, e => e.Kind is LinkEventKind.HeaderPointerMismatch or LinkEventKind.InvalidPacketHeader or LinkEventKind.FrameRejected);

        // 끊김은 카운트가 실제로 건너뛴 자리(198→200 · 200→208 · 211→219 · 224→229 · 231→234 · 241→248)에서만 난다.
        int counterGaps = counts.Zip(counts.Skip(1), (a, b) => b - a).Count(step => step != 1);
        Assert.Equal(6, counterGaps);
        Assert.Equal(counterGaps, events.Count(e => e.Kind == LinkEventKind.FrameGap));
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
