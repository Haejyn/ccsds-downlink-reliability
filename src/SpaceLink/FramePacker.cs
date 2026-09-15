namespace SpaceLink;

/// <summary>
/// 위성 쪽 역할: 한 가상 채널의 패킷 흐름을 고정 길이 전송 프레임으로 나눠 담는다.
/// 수신 처리기를 시험하기 위한 송신 모델이며, 마지막 프레임의 남는 공간은 유휴 패킷(APID 0x7FF)으로 채운다.
/// </summary>
public sealed class FramePacker
{
    private readonly FrameConfig _config;
    private readonly ushort _spacecraftId;
    private readonly byte _virtualChannelId;
    private byte _frameCount;

    public FramePacker(FrameConfig config, ushort spacecraftId, byte virtualChannelId, byte firstFrameCount = 0)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(spacecraftId, (ushort)TransferFrame.MaxSpacecraftId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(virtualChannelId, (byte)TransferFrame.MaxVirtualChannelId);
        _config = config;
        _spacecraftId = spacecraftId;
        _virtualChannelId = virtualChannelId;
        _frameCount = firstFrameCount;
    }

    /// <summary>패킷들을 순서대로 프레임에 담는다. 마지막 프레임은 유휴 패킷으로 채워 경계를 맞춘다.</summary>
    public IReadOnlyList<TransferFrame> Pack(IEnumerable<SpacePacket> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);
        var stream = new List<byte>();
        var starts = new List<int>();
        foreach (SpacePacket packet in packets)
        {
            starts.Add(stream.Count);
            stream.AddRange(packet.Encode());
        }

        int length = _config.DataFieldLength;
        int remainder = stream.Count % length;
        if (remainder != 0)
        {
            int free = length - remainder;
            // 유휴 패킷은 헤더 6 + 데이터 1 바이트 이상이어야 하므로, 남는 공간이 모자라면 다음 프레임까지 늘린다
            int idleTotal = free >= SpacePacket.PrimaryHeaderLength + 1 ? free : free + length;
            starts.Add(stream.Count);
            stream.AddRange(new SpacePacket(SpacePacket.IdleApid, 0, new byte[idleTotal - SpacePacket.PrimaryHeaderLength]).Encode());
        }

        var frames = new List<TransferFrame>(stream.Count / length);
        int nextStart = 0;
        for (int offset = 0; offset < stream.Count; offset += length)
        {
            while (nextStart < starts.Count && starts[nextStart] < offset)
            {
                nextStart++;
            }

            ushort fhp = nextStart < starts.Count && starts[nextStart] < offset + length
                ? (ushort)(starts[nextStart] - offset)
                : FirstHeaderPointer.NoPacketStart;
            byte[] data = stream.GetRange(offset, length).ToArray();
            frames.Add(new TransferFrame(_spacecraftId, _virtualChannelId, _frameCount, _frameCount, fhp, data));
            _frameCount = unchecked((byte)(_frameCount + 1));
        }

        return frames;
    }

    /// <summary>유휴 데이터만 담은 프레임 (OID, 제1 헤더 포인터 0x7FE).</summary>
    public TransferFrame IdleFrame()
    {
        var frame = new TransferFrame(_spacecraftId, _virtualChannelId, _frameCount, _frameCount,
            FirstHeaderPointer.IdleData, new byte[_config.DataFieldLength]);
        _frameCount = unchecked((byte)(_frameCount + 1));
        return frame;
    }
}
