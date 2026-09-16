using System.Diagnostics;
using System.Globalization;

namespace SpaceLink.Tests;

public class NominalReceptionTests
{
    [Fact]
    [Trait("Requirement", "REQ-EXT-01")]
    public void Packets_of_any_length_are_reassembled_exactly_and_in_order()
    {
        var rnd = new Random(1);
        List<SpacePacket> sent = Downlink.RandomPackets(rnd, count: 3_000, maxDataLength: 700, 10, 20, 30);
        Downlink.Link link = Downlink.Pack(sent);
        var events = new List<LinkEvent>();
        List<SpacePacket> received = Downlink.Receive(link.Frames, events);
        Assert.Equal(sent, received);
        Assert.Empty(events);
        Assert.True(link.Frames.Count > 256 * 3, "the run must wrap the 8-bit frame count several times");
    }

    [Fact]
    [Trait("Requirement", "REQ-EXT-01")]
    public void Packet_header_split_across_a_frame_boundary_is_reassembled()
    {
        // 데이터 필드 120 바이트: 첫 패킷 117 바이트 → 다음 헤더가 3 바이트 / 3 바이트로 쪼개진다
        SpacePacket first = Downlink.Packet(1, 0, 111);
        SpacePacket second = Downlink.Packet(1, 1, 50);
        Downlink.Link link = Downlink.Pack([first, second]);
        var events = new List<LinkEvent>();
        Assert.Equal(new[] { first, second }, Downlink.Receive(link.Frames, events));
        Assert.Empty(events);
    }

    [Fact]
    [Trait("Requirement", "REQ-EXT-02")]
    public void Virtual_channels_are_reassembled_independently_when_interleaved()
    {
        var rnd = new Random(2);
        List<SpacePacket> vc1 = Downlink.RandomPackets(rnd, 400, 300, 100);
        List<SpacePacket> vc2 = Downlink.RandomPackets(rnd, 400, 300, 200);
        Downlink.Link a = Downlink.Pack(vc1, virtualChannelId: 1);
        Downlink.Link b = Downlink.Pack(vc2, virtualChannelId: 2);
        var interleaved = new List<byte[]>();
        int i = 0, j = 0;
        while (i < a.Frames.Count || j < b.Frames.Count)
        {
            bool takeA = j >= b.Frames.Count || (i < a.Frames.Count && rnd.Next(2) == 0);
            interleaved.Add(takeA ? a.Frames[i++] : b.Frames[j++]);
        }

        var events = new List<LinkEvent>();
        List<SpacePacket> received = Downlink.Receive(interleaved, events);
        Assert.Equal(vc1, received.Where(p => p.Apid == 100));
        Assert.Equal(vc2, received.Where(p => p.Apid == 200));
        Assert.Empty(events);
    }

    [Fact]
    [Trait("Requirement", "REQ-EXT-01")]
    public void Idle_frames_between_bursts_do_not_disturb_reassembly()
    {
        var packer = new FramePacker(Downlink.Config, 0x155, 1);
        var rnd = new Random(3);
        var sent = new List<SpacePacket>();
        var frames = new List<byte[]>();
        for (int burst = 0; burst < 20; burst++)
        {
            List<SpacePacket> packets = Downlink.RandomPackets(rnd, 15, 200, (ushort)(burst + 1));
            sent.AddRange(packets);
            frames.AddRange(packer.Pack(packets).Select(f => f.Encode(Downlink.Config)));
            frames.Add(packer.IdleFrame().Encode(Downlink.Config));
        }

        var events = new List<LinkEvent>();
        Assert.Equal(sent, Downlink.Receive(frames, events));
        Assert.All(events, e => Assert.Equal(LinkEventKind.IdleFrame, e.Kind));
        Assert.Equal(20, events.Count);
    }

    [Fact]
    [Trait("Requirement", "REQ-EXT-05")]
    public void Packet_sequence_count_gaps_are_reported_per_apid()
    {
        SpacePacket[] sent = [Downlink.Packet(7, 0, 10), Downlink.Packet(7, 1, 10), Downlink.Packet(7, 3, 10), Downlink.Packet(8, 0, 10)];
        var events = new List<LinkEvent>();
        Assert.Equal(sent, Downlink.Receive(Downlink.Pack(sent).Frames, events));
        LinkEvent gap = Assert.Single(events);
        Assert.Equal(LinkEventKind.SequenceGap, gap.Kind);
        Assert.Contains("apid=7 expected=2 got=3", gap.Detail, StringComparison.Ordinal);
    }
}

/// <summary>
/// 결함 주입 — 수신기의 핵심 계약: (1) 손상된 패킷을 절대 내보내지 않는다 (2) 영향받지 않은 패킷은 모두 복구한다.
/// 기대 출력 = 손상·유실된 프레임과 한 바이트도 겹치지 않는 원본 패킷 전부, 원래 순서대로.
/// </summary>
public class FaultInjectionTests
{
    private static List<SpacePacket> ExpectedSurvivors(List<SpacePacket> sent, Downlink.Link link, ISet<int> damagedFrames) =>
        sent.Where((_, i) => !damagedFrames.Any(k => link.Overlaps(i, k))).ToList();

    [Fact]
    [Trait("Requirement", "REQ-EXT-09")]
    public void Invalid_packet_version_desynchronizes_until_the_next_first_header_pointer()
    {
        // 버전이 0 이 아닌 헤더를 만나면 그 채널의 조립 데이터를 버려야 한다. 버리지 않으면
        // 이어지는 프레임이 같은 쓰레기 버퍼에 계속 붙어 같은 이벤트가 되풀이된다.
        FrameConfig config = Downlink.Config;
        int dataFieldLength = config.DataFieldLength;
        byte[] bad = new SpacePacket(3, 0, new byte[200]).Encode();
        bad[0] |= 0x20;                                                  // 패킷 버전 번호 ≠ 0

        var continuation = new byte[dataFieldLength];
        bad.AsSpan(dataFieldLength).CopyTo(continuation);                // 나머지는 다음 프레임에 이어진다

        byte[] good = Downlink.Packet(4, 0, dataFieldLength - SpacePacket.PrimaryHeaderLength).Encode();
        Assert.Equal(dataFieldLength, good.Length);                      // 패딩이 남지 않도록 데이터 필드를 꽉 채운다

        byte[][] frames =
        [
            new TransferFrame(0x155, 1, 0, 0, 0, bad.AsSpan(0, dataFieldLength)).Encode(config),
            new TransferFrame(0x155, 1, 1, 1, FirstHeaderPointer.NoPacketStart, continuation).Encode(config),
            new TransferFrame(0x155, 1, 2, 2, 0, good).Encode(config),
        ];

        var events = new List<LinkEvent>();
        List<SpacePacket> received = Downlink.Receive(frames, events);
        Assert.Single(events, e => e.Kind == LinkEventKind.InvalidPacketHeader);
        Assert.Equal(new[] { SpacePacket.Decode(good) }, received);
    }

    [Fact]
    [Trait("Requirement", "REQ-EXT-03")]
    public void Frame_gap_event_reports_how_many_frames_were_lost()
    {
        // 유실 개수는 8 비트 순환을 고려한 (받은 카운트 − 기대 카운트) 다. 개수를 확인하지 않으면
        // 이 계산이 틀려도 시험이 통과한다.
        var rnd = new Random(41);
        List<SpacePacket> sent = Downlink.RandomPackets(rnd, count: 60, maxDataLength: 100, 9);
        Downlink.Link link = Downlink.Pack(sent);
        Assert.True(link.Frames.Count > 12, "the run needs frames on both sides of the gap");
        int[] dropped = [5, 6, 7];
        var events = new List<LinkEvent>();
        Downlink.Receive(link.Frames.Where((_, i) => !dropped.Contains(i)), events);
        LinkEvent gap = Assert.Single(events, e => e.Kind == LinkEventKind.FrameGap);
        Assert.Contains("expected=5 got=8 lost=3", gap.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Requirement", "REQ-EXT-03")]
    [InlineData(11, 0.02)]
    [InlineData(12, 0.05)]
    [InlineData(13, 0.20)]
    public void Dropped_frames_lose_only_the_packets_they_carried(int seed, double dropRate)
    {
        var rnd = new Random(seed);
        List<SpacePacket> sent = Downlink.RandomPackets(rnd, 2_000, 400, 1, 2);
        Downlink.Link link = Downlink.Pack(sent);
        var dropped = new HashSet<int>(Enumerable.Range(0, link.Frames.Count).Where(_ => rnd.NextDouble() < dropRate));
        var events = new List<LinkEvent>();
        List<SpacePacket> received = Downlink.Receive(link.Frames.Where((_, k) => !dropped.Contains(k)), events);

        Assert.Equal(ExpectedSurvivors(sent, link, dropped), received);
        // 갭 이벤트는 "앞서 받은 프레임이 있고, 그 다음 받은 프레임이 연속이 아닐 때" 한 번씩 난다
        int[] receivedIndices = Enumerable.Range(0, link.Frames.Count).Where(k => !dropped.Contains(k)).ToArray();
        int expectedGaps = receivedIndices.Zip(receivedIndices.Skip(1)).Count(pair => pair.Second != pair.First + 1);
        Assert.Equal(expectedGaps, events.Count(e => e.Kind == LinkEventKind.FrameGap));
    }

    [Theory]
    [Trait("Requirement", "REQ-EXT-03")]
    [InlineData(21)]
    [InlineData(22)]
    public void Bit_errors_are_caught_by_crc_and_behave_like_lost_frames(int seed)
    {
        var rnd = new Random(seed);
        List<SpacePacket> sent = Downlink.RandomPackets(rnd, 2_000, 400, 1);
        Downlink.Link link = Downlink.Pack(sent);
        var corrupted = new HashSet<int>();
        var frames = new List<byte[]>();
        for (int k = 0; k < link.Frames.Count; k++)
        {
            byte[] f = (byte[])link.Frames[k].Clone();
            if (rnd.NextDouble() < 0.05)
            {
                corrupted.Add(k);
                for (int e = rnd.Next(1, 4); e > 0; e--)
                {
                    int bit = rnd.Next(f.Length * 8);
                    f[bit / 8] ^= (byte)(0x80 >> (bit % 8));
                }
            }

            frames.Add(f);
        }

        var events = new List<LinkEvent>();
        List<SpacePacket> received = Downlink.Receive(frames, events);
        Assert.Equal(ExpectedSurvivors(sent, link, corrupted), received);
        Assert.Equal(corrupted.Count, events.Count(e => e.Kind == LinkEventKind.FrameRejected));
    }

    [Fact]
    [Trait("Requirement", "REQ-EXT-04")]
    public void Duplicated_frames_are_ignored_without_duplicating_packets()
    {
        var rnd = new Random(31);
        List<SpacePacket> sent = Downlink.RandomPackets(rnd, 1_000, 400, 1);
        Downlink.Link link = Downlink.Pack(sent);
        var frames = new List<byte[]>();
        int duplicates = 0;
        foreach (byte[] f in link.Frames)
        {
            frames.Add(f);
            if (rnd.NextDouble() < 0.1)
            {
                frames.Add(f);
                duplicates++;
            }
        }

        var events = new List<LinkEvent>();
        Assert.Equal(sent, Downlink.Receive(frames, events));
        Assert.Equal(duplicates, events.Count(e => e.Kind == LinkEventKind.DuplicateFrame));
    }

    [Fact]
    [Trait("Requirement", "REQ-EXT-03")]
    public void Reordered_frames_never_produce_a_corrupted_packet()
    {
        var rnd = new Random(41);
        List<SpacePacket> sent = Downlink.RandomPackets(rnd, 2_000, 400, 1);
        Downlink.Link link = Downlink.Pack(sent);
        var frames = new List<byte[]>(link.Frames);
        for (int k = 0; k + 1 < frames.Count; k++)
        {
            if (rnd.NextDouble() < 0.05)
            {
                (frames[k], frames[k + 1]) = (frames[k + 1], frames[k]);
                k++;
            }
        }

        var events = new List<LinkEvent>();
        List<SpacePacket> received = Downlink.Receive(frames, events);
        var original = new HashSet<SpacePacket>(sent);
        Assert.All(received, p => Assert.Contains(p, original));
        Assert.Contains(events, e => e.Kind == LinkEventKind.FrameGap);
    }

    /// <summary>256 장 단위 연속 유실을 시도마다 무작위 위치에 주입하고, 원본에 없는 패킷이 몇 개 나오는지 센다.</summary>
    private static int CorruptedPacketsAfterUndetectedLoss(int seed, int lost, bool withErrorControl)
    {
        var rnd = new Random(seed);
        List<SpacePacket> sent = withErrorControl
            ? Downlink.RandomPacketsWithErrorControl(rnd, 6_000, 500, 1, 2, 3)
            : Downlink.RandomPackets(rnd, 6_000, 500, 1, 2, 3);
        Downlink.Link link = Downlink.Pack(sent);
        var original = new HashSet<SpacePacket>(sent);
        int corrupted = 0;
        for (int trial = 0; trial < 40; trial++)
        {
            int start = rnd.Next(1, link.Frames.Count - lost - 1);
            var dropped = new HashSet<int>(Enumerable.Range(start, lost));
            var events = new List<LinkEvent>();
            var extractor = new PacketExtractor(Downlink.Config, verifyPacketErrorControl: withErrorControl);
            List<SpacePacket> received = Downlink.Receive(link.Frames.Where((_, k) => !dropped.Contains(k)), events, extractor);
            Assert.DoesNotContain(events, e => e.Kind == LinkEventKind.FrameGap);    // 카운트로는 보이지 않는다
            corrupted += received.Count(p => !original.Contains(p));
        }

        return corrupted;
    }

    [Fact]
    [Trait("Requirement", "REQ-EXT-06")]
    public void Defect_C1_without_packet_error_control_undetectable_frame_loss_emits_corrupted_packets()
    {
        // [결함 C-1] 첫 설계(프레임 CRC·카운트·제1 헤더 포인터만 사용)가 이 조건에서 손상 패킷을 내보냈다.
        // 프레임 계층만으로는 원리적으로 막을 수 없음을 고정해 두는 시험.
        int corrupted = 0;
        foreach (int seed in new[] { 51, 53, 54, 55 })
        {
            corrupted += CorruptedPacketsAfterUndetectedLoss(seed, 256, withErrorControl: false);
        }

        Assert.True(corrupted > 0, "frame-layer checks alone were expected to let corrupted packets through");
    }

    [Theory]
    [Trait("Requirement", "REQ-EXT-06")]
    [InlineData(51, 256)]
    [InlineData(52, 512)]
    [InlineData(53, 256)]
    [InlineData(54, 256)]
    [InlineData(55, 256)]
    public void With_packet_error_control_undetectable_frame_loss_never_emits_a_corrupted_packet(int seed, int lost)
    {
        Assert.Equal(0, CorruptedPacketsAfterUndetectedLoss(seed, lost, withErrorControl: true));
    }

    [Fact]
    [Trait("Requirement", "REQ-EXT-06")]
    public void Packet_error_control_rejects_a_packet_with_any_single_bit_error()
    {
        byte[] good = Downlink.PacketWithErrorControl(9, 1, 200).Encode();
        Assert.True(SpacePacket.Decode(good).HasValidErrorControl());
        for (int bit = 0; bit < good.Length * 8; bit++)
        {
            byte[] bad = (byte[])good.Clone();
            bad[bit / 8] ^= (byte)(0x80 >> (bit % 8));
            SpacePacket parsed;
            try
            {
                parsed = SpacePacket.Decode(bad);
            }
            catch (FormatException)
            {
                continue;                                                     // 헤더 오류는 형식 검사에서 먼저 걸린다
            }

            Assert.False(parsed.HasValidErrorControl(), $"bit {bit}");
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-EXT-01")]
    public void Nominal_reception_with_packet_error_control_matches_input()
    {
        var rnd = new Random(56);
        List<SpacePacket> sent = Downlink.RandomPacketsWithErrorControl(rnd, 2_000, 500, 1, 2);
        var events = new List<LinkEvent>();
        var extractor = new PacketExtractor(Downlink.Config, verifyPacketErrorControl: true);
        Assert.Equal(sent, Downlink.Receive(Downlink.Pack(sent).Frames, events, extractor));
        Assert.Empty(events);
    }
}

public class RobustnessTests
{
    [Fact]
    [Trait("Requirement", "REQ-EXT-07")]
    public void Arbitrary_bytes_never_crash_the_receiver()
    {
        var rnd = new Random(61);
        var extractor = new PacketExtractor(Downlink.Config);
        for (int i = 0; i < 50_000; i++)
        {
            var bytes = new byte[rnd.Next(0, 300)];
            rnd.NextBytes(bytes);
            ExtractionResult result = extractor.Process(bytes);
            Assert.Empty(result.Packets);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-EXT-07")]
    public void Frames_with_valid_crc_but_random_content_never_crash_the_receiver()
    {
        var rnd = new Random(62);
        var extractor = new PacketExtractor(Downlink.Config);
        for (int i = 0; i < 50_000; i++)
        {
            var frame = new byte[Downlink.Config.FrameLength];
            rnd.NextBytes(frame);
            frame[0] &= 0x3F;                                               // 버전 00
            frame[4] = (byte)((frame[4] & 0x07) | 0x18);                   // 동기 0, 세그먼트 11
            int fhp = rnd.Next(3) switch { 0 => 0x7FF, 1 => 0x7FE, _ => rnd.Next(Downlink.Config.DataFieldLength) };
            frame[4] = (byte)((frame[4] & 0xF8) | (fhp >> 8));
            frame[5] = (byte)fhp;
            frame[1] &= 0xFE;                                               // OCF 없음
            ushort crc = Crc16Ccitt.Compute(frame.AsSpan(0, frame.Length - 2));
            frame[^2] = (byte)(crc >> 8);
            frame[^1] = (byte)crc;
            _ = extractor.Process(frame);
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    [Trait("Requirement", "REQ-EXT-08")]
    public void Throughput_is_measured_and_does_not_regress_below_5k_frames_per_second()
    {
        var rnd = new Random(71);
        Downlink.Link link = Downlink.Pack(Downlink.RandomPackets(rnd, 60_000, 300, 1, 2, 3, 4));
        var extractor = new PacketExtractor(Downlink.Config);
        var sw = Stopwatch.StartNew();
        int packets = 0;
        foreach (byte[] f in link.Frames)
        {
            packets += extractor.Process(f).Packets.Count;
        }

        sw.Stop();
        double framesPerSecond = link.Frames.Count / sw.Elapsed.TotalSeconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"throughput: {link.Frames.Count} frames ({link.Frames.Count * 128 / 1_000_000.0:F1} MB) in {sw.Elapsed.TotalMilliseconds:F0} ms = {framesPerSecond:F0} frames/s"));
        Assert.Equal(60_000, packets);
        // 합격 기준이 아니라 측정 항목: 목표(Gbps 급 다운링크)와의 차이는 보고서에 기록한다. 큰 성능 회귀만 막는다.
        Assert.True(framesPerSecond > 5_000, $"{framesPerSecond:F0} frames/s");
    }
}
