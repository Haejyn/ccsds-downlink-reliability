using BenchmarkDotNet.Attributes;

namespace SpaceLink.Benchmarks;

/// <summary>
/// 수신 처리기 정상 수신 경로: 프레임을 한 장씩 넣어 패킷을 재조립한다 — **추출 단계만**이다.
/// ASM 동기·PN 되돌림·RS 복호는 들어 있지 않다 (그 체인은 <see cref="ChannelChainBenchmark"/>).
/// </summary>
/// <remarks>
/// 두 메서드는 <c>OperationsPerInvoke</c> 로 프레임 수를 BenchmarkDotNet 에 알려, 시간과 할당을 **프레임당** 값으로 보고한다.
/// 그래서 CI 게이트(<c>tools/check_allocation.py</c>)는 프레임 수를 따로 알 필요가 없다.
/// </remarks>
[MemoryDiagnoser]
public class ExtractorBenchmark
{
    /// <summary>한 번에 흘려보내는 패킷 수.</summary>
    private const int PacketCount = 60_000;

    /// <summary>
    /// 패킷 60,000 개를 128 바이트 프레임으로 포장하면 나오는 장수 (약 10 MB).
    /// 예전에는 78,001 이라고 적었는데 그것은 옛 xUnit 처리량 측정(다른 입력)에서 가져온 값이었다 — 이 입력의 실제는 78,089 다.
    /// </summary>
    public const int Frames = 78_089;

    /// <summary>PEC 2 바이트가 패킷마다 더 붙어 프레임이 더 나온다.</summary>
    public const int FramesWithErrorControl = 79_089;

    private byte[][] _frames = [];
    private byte[][] _framesWithErrorControl = [];

    [GlobalSetup]
    public void Setup()
    {
        _frames = SampleTraffic.Frames(SampleTraffic.Packets(PacketCount, withErrorControl: false));
        _framesWithErrorControl = SampleTraffic.Frames(SampleTraffic.Packets(PacketCount, withErrorControl: true));

        // 상수가 실제 장수와 어긋나면 게이트가 엉뚱한 수로 나눈다 — 조용히 틀리느니 여기서 멈춘다.
        if (_frames.Length != Frames || _framesWithErrorControl.Length != FramesWithErrorControl)
        {
            throw new InvalidOperationException(
                $"프레임 수가 상수와 다르다: {_frames.Length} (상수 {Frames}) / {_framesWithErrorControl.Length} (상수 {FramesWithErrorControl})");
        }
    }

    /// <summary>PEC 없이 재조립 — 프레임 계층 검증 + 패킷 조립 비용.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = Frames)]
    public int Reassemble()
    {
        var extractor = new PacketExtractor(SampleTraffic.Config);
        int packets = 0;
        foreach (byte[] frame in _frames)
        {
            packets += extractor.Process(frame).Packets.Count;
        }

        return packets;
    }

    /// <summary>PEC 검증까지 — 결함 C-1 을 막는 경로의 비용.</summary>
    [Benchmark(OperationsPerInvoke = FramesWithErrorControl)]
    public int ReassembleWithErrorControl()
    {
        var extractor = new PacketExtractor(SampleTraffic.Config, verifyPacketErrorControl: true);
        int packets = 0;
        foreach (byte[] frame in _framesWithErrorControl)
        {
            packets += extractor.Process(frame).Packets.Count;
        }

        return packets;
    }
}
