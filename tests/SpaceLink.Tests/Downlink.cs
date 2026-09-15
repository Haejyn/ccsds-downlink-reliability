namespace SpaceLink.Tests;

/// <summary>시험용 송신 모델: 패킷을 만들고, 프레임에 담고, 각 패킷이 프레임 흐름에서 차지하는 구간을 기록한다.</summary>
internal static class Downlink
{
    /// <summary>프레임 128 바이트 = 헤더 6 + 데이터 120 + FECF 2.</summary>
    public static readonly FrameConfig Config = new(frameLength: 128);

    /// <summary>(APID, 순서 카운트, 길이) 에서 결정되는 내용 — 출력 패킷이 원본과 한 바이트라도 다르면 드러난다.</summary>
    public static SpacePacket Packet(ushort apid, ushort sequenceCount, int dataLength)
    {
        var data = new byte[dataLength];
        new Random((apid * 65_536) + sequenceCount + (dataLength * 7_919)).NextBytes(data);
        return new SpacePacket(apid, sequenceCount, data);
    }

    /// <summary>같은 규칙으로 만든 사용자 데이터에 PEC 를 붙인 패킷.</summary>
    public static SpacePacket PacketWithErrorControl(ushort apid, ushort sequenceCount, int userDataLength)
    {
        var data = new byte[userDataLength];
        new Random((apid * 65_536) + sequenceCount + (userDataLength * 7_919)).NextBytes(data);
        return SpacePacket.WithErrorControl(apid, sequenceCount, data);
    }

    public static List<SpacePacket> RandomPacketsWithErrorControl(Random rnd, int count, int maxUserDataLength, params ushort[] apids)
    {
        var next = apids.ToDictionary(a => a, _ => (ushort)0);
        var packets = new List<SpacePacket>(count);
        for (int i = 0; i < count; i++)
        {
            ushort apid = apids[rnd.Next(apids.Length)];
            packets.Add(PacketWithErrorControl(apid, next[apid], rnd.Next(1, maxUserDataLength + 1)));
            next[apid] = (ushort)((next[apid] + 1) & SpacePacket.MaxSequenceCount);
        }

        return packets;
    }

    public static List<SpacePacket> RandomPackets(Random rnd, int count, int maxDataLength, params ushort[] apids)
    {
        var next = apids.ToDictionary(a => a, _ => (ushort)0);
        var packets = new List<SpacePacket>(count);
        for (int i = 0; i < count; i++)
        {
            ushort apid = apids[rnd.Next(apids.Length)];
            packets.Add(Packet(apid, next[apid], rnd.Next(1, maxDataLength + 1)));
            next[apid] = (ushort)((next[apid] + 1) & SpacePacket.MaxSequenceCount);
        }

        return packets;
    }

    public sealed record Link(List<byte[]> Frames, List<(int Start, int End)> PacketRanges, int DataFieldLength)
    {
        /// <summary>패킷 i 가 프레임 k 의 데이터 필드와 한 바이트라도 겹치는가.</summary>
        public bool Overlaps(int packetIndex, int frameIndex)
        {
            (int start, int end) = PacketRanges[packetIndex];
            return start < (frameIndex + 1) * DataFieldLength && end > frameIndex * DataFieldLength;
        }
    }

    public static Link Pack(IReadOnlyList<SpacePacket> packets, byte virtualChannelId = 1, byte firstFrameCount = 0)
    {
        var packer = new FramePacker(Config, spacecraftId: 0x155, virtualChannelId, firstFrameCount);
        var ranges = new List<(int, int)>(packets.Count);
        int offset = 0;
        foreach (SpacePacket p in packets)
        {
            ranges.Add((offset, offset + p.TotalLength));
            offset += p.TotalLength;
        }

        List<byte[]> frames = packer.Pack(packets).Select(f => f.Encode(Config)).ToList();
        return new Link(frames, ranges, Config.DataFieldLength);
    }

    public static List<SpacePacket> Receive(IEnumerable<byte[]> frames, List<LinkEvent> events, PacketExtractor? extractor = null)
    {
        extractor ??= new PacketExtractor(Config);
        var received = new List<SpacePacket>();
        foreach (byte[] frame in frames)
        {
            ExtractionResult result = extractor.Process(frame);
            received.AddRange(result.Packets);
            events.AddRange(result.Events);
        }

        return received;
    }

    /// <summary>표준 비트 단위 CRC-16/CCITT-FALSE — 표 기반 구현과 독립적인 기준.</summary>
    public static ushort ReferenceCrc(ReadOnlySpan<byte> data)
    {
        int crc = 0xFFFF;
        foreach (byte b in data)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                bool msb = ((crc >> 15) & 1) == 1;
                bool inBit = ((b >> bit) & 1) == 1;
                crc = (crc << 1) & 0xFFFF;
                if (msb ^ inBit)
                {
                    crc ^= 0x1021;
                }
            }
        }

        return (ushort)crc;
    }
}
