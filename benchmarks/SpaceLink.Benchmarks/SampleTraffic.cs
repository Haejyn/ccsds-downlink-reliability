namespace SpaceLink.Benchmarks;

/// <summary>
/// 두 벤치마크가 같이 쓰는 입력. 내용·길이가 (APID, 순서 카운트) 로 결정되도록 고정 시드를 쓴다 — 실행마다 같은 입력.
/// 프레임 128 바이트(헤더 6 + 데이터 120 + FECF 2) — 시험에서 쓰는 형식과 같다.
/// </summary>
internal static class SampleTraffic
{
    public const int FrameLength = 128;

    public static readonly FrameConfig Config = new(FrameLength);

    public static List<SpacePacket> Packets(int count, bool withErrorControl)
    {
        var rnd = new Random(71);
        ushort[] apids = [1, 2, 3, 4];
        var next = new ushort[apids.Length];
        var packets = new List<SpacePacket>(count);
        for (int i = 0; i < count; i++)
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

    public static byte[][] Frames(List<SpacePacket> packets)
    {
        var packer = new FramePacker(Config, spacecraftId: 0x155, virtualChannelId: 1);
        return packer.Pack(packets).Select(f => f.Encode(Config)).ToArray();
    }
}
