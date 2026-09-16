using BenchmarkDotNet.Attributes;

namespace SpaceLink.Benchmarks;

/// <summary>
/// 수신 처리기 정상 수신 경로: 프레임을 한 장씩 넣어 패킷을 재조립한다.
/// 프레임 128 바이트(헤더 6 + 데이터 120 + FECF 2) — 시험에서 쓰는 형식과 같다.
/// </summary>
[MemoryDiagnoser]
public class ExtractorBenchmark
{
    private static readonly FrameConfig Config = new(frameLength: 128);

    private byte[][] _frames = [];
    private byte[][] _framesWithErrorControl = [];

    /// <summary>한 번에 흘려보내는 패킷 수 (프레임 약 78,000 장 · 10 MB).</summary>
    [Params(60_000)]
    public int PacketCount { get; set; }

    public int FrameCount => _frames.Length;

    [GlobalSetup]
    public void Setup()
    {
        _frames = Pack(BuildPackets(withErrorControl: false));
        _framesWithErrorControl = Pack(BuildPackets(withErrorControl: true));
    }

    /// <summary>PEC 없이 재조립 — 프레임 계층 검증 + 패킷 조립 비용.</summary>
    [Benchmark(Baseline = true)]
    public int Reassemble()
    {
        var extractor = new PacketExtractor(Config);
        int packets = 0;
        foreach (byte[] frame in _frames)
        {
            packets += extractor.Process(frame).Packets.Count;
        }

        return packets;
    }

    /// <summary>PEC 검증까지 — 결함 C-1 을 막는 경로의 비용.</summary>
    [Benchmark]
    public int ReassembleWithErrorControl()
    {
        var extractor = new PacketExtractor(Config, verifyPacketErrorControl: true);
        int packets = 0;
        foreach (byte[] frame in _framesWithErrorControl)
        {
            packets += extractor.Process(frame).Packets.Count;
        }

        return packets;
    }

    private List<SpacePacket> BuildPackets(bool withErrorControl)
    {
        // 내용·길이가 (APID, 순서 카운트) 로 결정되도록 고정 시드를 쓴다 — 실행마다 같은 입력.
        var rnd = new Random(71);
        ushort[] apids = [1, 2, 3, 4];
        var next = new ushort[apids.Length];
        var packets = new List<SpacePacket>(PacketCount);
        for (int i = 0; i < PacketCount; i++)
        {
            int slot = rnd.Next(apids.Length);
            var data = new byte[rnd.Next(1, 301)];
            rnd.NextBytes(data);
            packets.Add(withErrorControl
                ? SpacePacket.WithErrorControl(apids[slot], next[slot], data)
                : new SpacePacket(apids[slot], next[slot], data));
            next[slot] = (ushort)((next[slot] + 1) & SpacePacket.MaxSequenceCount);
        }

        return packets;
    }

    private static byte[][] Pack(List<SpacePacket> packets)
    {
        var packer = new FramePacker(Config, spacecraftId: 0x155, virtualChannelId: 1);
        return packer.Pack(packets).Select(f => f.Encode(Config)).ToArray();
    }
}
