namespace SpaceLink;

public enum LinkEventKind
{
    /// <summary>프레임 검증 실패 (길이·버전·CRC·데이터 필드 상태). 가상 채널을 믿을 수 없어 어느 채널에도 반영하지 않는다.</summary>
    FrameRejected,

    /// <summary>직전 프레임과 같은 가상 채널 프레임 카운트 — 무시한다.</summary>
    DuplicateFrame,

    /// <summary>가상 채널 프레임 카운트가 건너뜀 — 진행 중이던 패킷을 버리고 다음 제1 헤더 포인터에서 다시 동기화.</summary>
    FrameGap,

    /// <summary>유휴 데이터 프레임.</summary>
    IdleFrame,

    /// <summary>제1 헤더 포인터 위치와 조립 중인 패킷 경계가 어긋남 — 조립 중 데이터를 버리고 포인터에서 다시 시작.</summary>
    HeaderPointerMismatch,

    /// <summary>패킷 버전 번호가 0 이 아님 — 동기를 잃은 것으로 보고 다음 포인터까지 버린다.</summary>
    InvalidPacketHeader,

    /// <summary>같은 APID 의 패킷 순서 카운트가 건너뜀.</summary>
    SequenceGap,

    /// <summary>
    /// 패킷 오류 제어(PEC) 불일치 — 프레임 카운트로 감지되지 않는 유실 등으로 패킷이 잘못 이어 붙여졌다.
    /// 패킷을 버리고 다음 제1 헤더 포인터까지 동기를 푼다.
    /// </summary>
    PacketErrorControlFailed,
}

public sealed record LinkEvent(LinkEventKind Kind, int? VirtualChannelId, string Detail);

public sealed record ExtractionResult(IReadOnlyList<SpacePacket> Packets, IReadOnlyList<LinkEvent> Events);

/// <summary>
/// 지상국 수신 처리: 전송 프레임을 한 장씩 받아 검증하고, 가상 채널별로 패킷을 재조립한다.
/// 설계 원칙 — 손상됐을 가능성이 있는 패킷은 절대 내보내지 않는다. 확실하지 않으면 버리고 이벤트로 알린다.
/// </summary>
public sealed class PacketExtractor
{
    private readonly FrameConfig _config;
    private readonly bool _verifyPacketErrorControl;
    private readonly ChannelState[] _channels = new ChannelState[TransferFrame.MaxVirtualChannelId + 1];
    private readonly Dictionary<ushort, ushort> _lastSequenceCount = new();

    /// <param name="config">임무 프레임 형식.</param>
    /// <param name="verifyPacketErrorControl">
    /// true 면 유휴 패킷이 아닌 모든 패킷의 PEC 를 검증한다. [결함 C-1] 이 검증이 없으면 256 장 단위 연속 유실에서
    /// 손상 패킷이 그대로 나간다 — 프레임 계층 정보(카운트·제1 헤더 포인터)만으로는 원리적으로 구분할 수 없다.
    /// </param>
    public PacketExtractor(FrameConfig config, bool verifyPacketErrorControl = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _verifyPacketErrorControl = verifyPacketErrorControl;
        for (int i = 0; i < _channels.Length; i++)
        {
            _channels[i] = new ChannelState();
        }
    }

    public ExtractionResult Process(ReadOnlySpan<byte> rawFrame)
    {
        var packets = new List<SpacePacket>();
        var events = new List<LinkEvent>();
        FrameDecodeResult decoded = TransferFrame.Decode(rawFrame, _config);
        if (!decoded.IsValid)
        {
            events.Add(new LinkEvent(LinkEventKind.FrameRejected, null, decoded.Error.ToString()));
            return new ExtractionResult(packets, events);
        }

        TransferFrame frame = decoded.Frame!;
        int vc = frame.VirtualChannelId;
        ChannelState channel = _channels[vc];
        byte count = frame.VirtualChannelFrameCount;

        if (channel.LastFrameCount is byte last)
        {
            if (count == last)
            {
                events.Add(new LinkEvent(LinkEventKind.DuplicateFrame, vc, $"count={count}"));
                return new ExtractionResult(packets, events);
            }

            byte expected = unchecked((byte)(last + 1));
            if (count != expected)
            {
                int lost = (count - expected + 256) % 256;
                events.Add(new LinkEvent(LinkEventKind.FrameGap, vc, $"expected={expected} got={count} lost={lost}"));
                channel.Desynchronize();
            }
        }

        channel.LastFrameCount = count;
        ushort fhp = frame.FirstHeaderPointerValue;
        ReadOnlySpan<byte> data = frame.DataField.Span;

        if (fhp == FirstHeaderPointer.IdleData)
        {
            events.Add(new LinkEvent(LinkEventKind.IdleFrame, vc, $"count={count}"));
            return new ExtractionResult(packets, events);
        }

        if (fhp == FirstHeaderPointer.NoPacketStart)
        {
            if (channel.InSync)
            {
                channel.Buffer.AddRange(data);
                Drain(channel, vc, packets, events);
            }

            return new ExtractionResult(packets, events);
        }

        if (channel.InSync)
        {
            channel.Buffer.AddRange(data[..fhp]);
            Drain(channel, vc, packets, events);
            if (channel.InSync && channel.Buffer.Count != 0)
            {
                events.Add(new LinkEvent(LinkEventKind.HeaderPointerMismatch, vc,
                    $"{channel.Buffer.Count} pending bytes did not end at first header pointer {fhp}"));
            }
        }

        channel.Resynchronize();
        channel.Buffer.AddRange(data[fhp..]);
        Drain(channel, vc, packets, events);
        return new ExtractionResult(packets, events);
    }

    private void Drain(ChannelState channel, int vc, List<SpacePacket> packets, List<LinkEvent> events)
    {
        while (channel.InSync && PacketHeader.TryRead(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(channel.Buffer), out PacketHeader header))
        {
            if (header.Version != 0)
            {
                events.Add(new LinkEvent(LinkEventKind.InvalidPacketHeader, vc, $"version={header.Version}"));
                channel.Desynchronize();
                return;
            }

            if (channel.Buffer.Count < header.TotalLength)
            {
                return;
            }

            SpacePacket packet = SpacePacket.Decode(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(channel.Buffer)[..header.TotalLength]);
            channel.Buffer.RemoveRange(0, header.TotalLength);
            if (packet.IsIdle)
            {
                continue;
            }

            if (_verifyPacketErrorControl && !packet.HasValidErrorControl())
            {
                events.Add(new LinkEvent(LinkEventKind.PacketErrorControlFailed, vc, $"apid={packet.Apid} seq={packet.SequenceCount}"));
                channel.Desynchronize();
                return;
            }

            CheckSequence(packet, events);
            packets.Add(packet);
        }
    }

    private void CheckSequence(SpacePacket packet, List<LinkEvent> events)
    {
        if (_lastSequenceCount.TryGetValue(packet.Apid, out ushort last))
        {
            int expected = (last + 1) & SpacePacket.MaxSequenceCount;
            if (packet.SequenceCount != expected)
            {
                events.Add(new LinkEvent(LinkEventKind.SequenceGap, null,
                    $"apid={packet.Apid} expected={expected} got={packet.SequenceCount}"));
            }
        }

        _lastSequenceCount[packet.Apid] = packet.SequenceCount;
    }

    private sealed class ChannelState
    {
        public byte? LastFrameCount { get; set; }

        public bool InSync { get; private set; }

        public List<byte> Buffer { get; } = [];

        public void Desynchronize()
        {
            InSync = false;
            Buffer.Clear();
        }

        public void Resynchronize()
        {
            InSync = true;
            Buffer.Clear();
        }
    }
}
