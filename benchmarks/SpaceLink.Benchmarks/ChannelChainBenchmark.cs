using BenchmarkDotNet.Attributes;
using SpaceLink.ChannelCoding;

namespace SpaceLink.Benchmarks;

/// <summary>
/// 수신 체인 전체: ASM 동기 → PN 되돌림 → RS 복호 → 패킷 추출. 지상국이 실제로 지나는 길이다.
/// <see cref="ExtractorBenchmark"/> 의 수치는 이 체인의 **마지막 단계만**이다.
/// </summary>
/// <remarks>
/// 송신(<c>EncodeCadu</c>)은 지상국의 일이 아니므로 <c>GlobalSetup</c> 에서 미리 해 두고 측정 구간에는 수신만 둔다.
/// 스트림은 소켓 한 번 읽기 크기의 조각(4 KB)으로 넣는다 — 한 번에 넣으면 동기기 버퍼 정리가 측정을 지배한다.
/// </remarks>
[MemoryDiagnoser]
public class ChannelChainBenchmark
{
    /// <summary>한 번의 측정에서 흘려보내는 CADU 수 = 프레임 수 (<c>OperationsPerInvoke</c> 와 같아야 한다).</summary>
    public const int Cadus = 2_000;

    /// <summary>수신 소켓 한 번 읽기 크기. CADU(128 B 프레임이면 164 B — 짧은 코드블록)와 나누어떨어지지 않게 둔다 — 조각 경계가 코드블록 한가운데를 가르도록.</summary>
    private const int ChunkBytes = 4_096;

    /// <summary>부호어당 주입하는 심볼 오류 수 — 정정 능력 t = 16 의 절반.</summary>
    private const int SymbolErrorsPerCodeword = 8;

    /// <summary>스트림 앞의 잡음 — 동기기가 Search → Check → Lock 을 한 번 실제로 밟게 한다.</summary>
    private const int LeadingNoiseBytes = 5;

    private static readonly ChannelCodec Codec = new(SampleTraffic.FrameLength, interleavingDepth: 1, randomize: true);

    private byte[] _clean = [];
    private byte[] _corrupted = [];

    [GlobalSetup]
    public void Setup()
    {
        byte[][] frames = SampleTraffic.Frames(SampleTraffic.Packets(count: 3_000, withErrorControl: false))
            .Take(Cadus).ToArray();
        if (frames.Length != Cadus)
        {
            throw new InvalidOperationException($"CADU {Cadus} 장을 만들 프레임이 모자란다: {frames.Length}");
        }

        var rnd = new Random(2026);
        var stream = new List<byte>(LeadingNoiseBytes + (Cadus * Codec.CaduLength));
        var noise = new byte[LeadingNoiseBytes];
        rnd.NextBytes(noise);
        stream.AddRange(noise);
        foreach (byte[] frame in frames)
        {
            stream.AddRange(Codec.EncodeCadu(frame));
        }

        _clean = [.. stream];
        _corrupted = InjectSymbolErrors(_clean, rnd);

        // 무엇을 재는지 확인한다 — 체인이 원래 패킷을 전부 복원하지 못하면 측정이 아니라 오작동이다.
        int expected = CountPackets(frames);
        if (Receive(_clean) != expected || Receive(_corrupted) != expected)
        {
            throw new InvalidOperationException("체인이 프레임을 전부 복원하지 못했다 — 측정 입력이 잘못됐다");
        }
    }

    /// <summary>오류 없음 — RS 가 신드롬 0 으로 곧바로 빠져나가는 경로. 정정 작업이 한 번도 돌지 않는다.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = Cadus)]
    public int DecodeChain() => Receive(_clean);

    /// <summary>부호어마다 심볼 오류 8 개 — Berlekamp-Massey · Chien · Forney 가 매번 돈다.</summary>
    [Benchmark(OperationsPerInvoke = Cadus)]
    public int DecodeChainWithSymbolErrors() => Receive(_corrupted);

    /// <summary>ASM 동기 → PN 되돌림 → RS 복호 → 패킷 추출. 수신기 상태는 호출마다 새로 만든다.</summary>
    private static int Receive(byte[] stream)
    {
        var synchronizer = new FrameSynchronizer(Codec.CodeblockLength);
        var extractor = new PacketExtractor(SampleTraffic.Config);
        int packets = 0;
        for (int offset = 0; offset < stream.Length; offset += ChunkBytes)
        {
            int length = Math.Min(ChunkBytes, stream.Length - offset);
            foreach (byte[] codeblock in synchronizer.Process(stream.AsSpan(offset, length)))
            {
                ChannelDecodeResult decoded = Codec.DecodeCodeblock(codeblock);
                if (decoded.Succeeded && decoded.TransferFrame is not null)
                {
                    packets += extractor.Process(decoded.TransferFrame).Packets.Count;
                }
            }
        }

        return packets;
    }

    private static int CountPackets(byte[][] frames)
    {
        var extractor = new PacketExtractor(SampleTraffic.Config);
        return frames.Sum(frame => extractor.Process(frame).Packets.Count);
    }

    /// <summary>모든 CADU 의 코드블록 안에서 서로 다른 8 위치를 0 이 아닌 값과 XOR 한다 (ASM 4 바이트는 건드리지 않는다).</summary>
    private static byte[] InjectSymbolErrors(byte[] clean, Random rnd)
    {
        byte[] corrupted = (byte[])clean.Clone();
        for (int cadu = 0; cadu < Cadus; cadu++)
        {
            int codeblockStart = LeadingNoiseBytes + (cadu * Codec.CaduLength) + FrameSynchronizer.MarkerLength;
            var positions = new HashSet<int>();
            while (positions.Count < SymbolErrorsPerCodeword)
            {
                positions.Add(rnd.Next(Codec.CodeblockLength));
            }

            foreach (int position in positions)
            {
                corrupted[codeblockStart + position] ^= (byte)rnd.Next(1, 256);
            }
        }

        return corrupted;
    }
}
