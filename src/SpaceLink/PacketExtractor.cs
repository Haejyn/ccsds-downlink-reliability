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
///
/// 정상 흐름에서는 프레임 대부분이 패킷도 이벤트도 내지 않는다. 그 경우 아무것도 할당하지 않는다
/// (빈 목록은 공유 인스턴스, 결과 객체도 하나를 재사용). 프레임 검증은 사본 없이 원본 버퍼 위에서 한다.
/// </summary>
public sealed class PacketExtractor
{
    private static readonly SpacePacket[] NoPackets = [];
    private static readonly LinkEvent[] NoEvents = [];
    private static readonly ExtractionResult Empty = new(NoPackets, NoEvents);

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
        List<SpacePacket>? packets = null;
        List<LinkEvent>? events = null;

        FrameError error = TransferFrame.Validate(rawFrame, _config, out FrameView frame);
        if (error != FrameError.None)
        {
            Add(ref events, new LinkEvent(LinkEventKind.FrameRejected, null, error.ToString()));
            return Result(packets, events);
        }

        int vc = frame.VirtualChannelId;
        ChannelState channel = _channels[vc];
        byte count = frame.VirtualChannelFrameCount;

        if (channel.LastFrameCount is byte last)
        {
            if (count == last)
            {
                Add(ref events, new LinkEvent(LinkEventKind.DuplicateFrame, vc, $"count={count}"));
                return Result(packets, events);
            }

            byte expected = unchecked((byte)(last + 1));
            if (count != expected)
            {
                int lost = (count - expected + 256) % 256;
                Add(ref events, new LinkEvent(LinkEventKind.FrameGap, vc, $"expected={expected} got={count} lost={lost}"));
                channel.Desynchronize();
            }
        }

        channel.LastFrameCount = count;
        ushort fhp = frame.FirstHeaderPointerValue;
        ReadOnlySpan<byte> data = frame.DataField;

        if (fhp == FirstHeaderPointer.IdleData)
        {
            Add(ref events, new LinkEvent(LinkEventKind.IdleFrame, vc, $"count={count}"));
            return Result(packets, events);
        }

        if (fhp == FirstHeaderPointer.NoPacketStart)
        {
            if (channel.InSync)
            {
                channel.Append(data);
                Drain(channel, vc, ref packets, ref events);
            }

            return Result(packets, events);
        }

        if (channel.InSync)
        {
            channel.Append(data[..fhp]);
            Drain(channel, vc, ref packets, ref events);
            if (channel.InSync && channel.Count != 0)
            {
                Add(ref events, new LinkEvent(LinkEventKind.HeaderPointerMismatch, vc,
                    $"{channel.Count} pending bytes did not end at first header pointer {fhp}"));
            }
        }

        channel.Resynchronize();
        channel.Append(data[fhp..]);
        Drain(channel, vc, ref packets, ref events);
        return Result(packets, events);
    }

    private static void Add<T>(ref List<T>? list, T item) => (list ??= []).Add(item);

    private static ExtractionResult Result(List<SpacePacket>? packets, List<LinkEvent>? events) =>
        packets is null && events is null
            ? Empty
            : new ExtractionResult((IReadOnlyList<SpacePacket>?)packets ?? NoPackets, (IReadOnlyList<LinkEvent>?)events ?? NoEvents);

    private void Drain(ChannelState channel, int vc, ref List<SpacePacket>? packets, ref List<LinkEvent>? events)
    {
        while (channel.InSync && PacketHeader.TryRead(channel.Pending, out PacketHeader header))
        {
            if (header.Version != 0)
            {
                Add(ref events, new LinkEvent(LinkEventKind.InvalidPacketHeader, vc, $"version={header.Version}"));
                channel.Desynchronize();
                return;
            }

            if (channel.Count < header.TotalLength)
            {
                return;
            }

            SpacePacket packet = SpacePacket.Decode(channel.Pending[..header.TotalLength]);
            channel.Consume(header.TotalLength);
            if (packet.IsIdle)
            {
                continue;
            }

            if (_verifyPacketErrorControl && !packet.HasValidErrorControl())
            {
                Add(ref events, new LinkEvent(LinkEventKind.PacketErrorControlFailed, vc, $"apid={packet.Apid} seq={packet.SequenceCount}"));
                channel.Desynchronize();
                return;
            }

            CheckSequence(packet, ref events);
            Add(ref packets, packet);
        }
    }

    private void CheckSequence(SpacePacket packet, ref List<LinkEvent>? events)
    {
        if (_lastSequenceCount.TryGetValue(packet.Apid, out ushort last))
        {
            int expected = (last + 1) & SpacePacket.MaxSequenceCount;
            if (packet.SequenceCount != expected)
            {
                Add(ref events, new LinkEvent(LinkEventKind.SequenceGap, null,
                    $"apid={packet.Apid} expected={expected} got={packet.SequenceCount}"));
            }
        }

        _lastSequenceCount[packet.Apid] = packet.SequenceCount;
    }

    /// <summary>
    /// 가상 채널 하나의 조립 상태. 조립 버퍼는 앞을 잘라내지 않고 <see cref="_start"/> 만 옮긴다 —
    /// `List&lt;byte&gt;.RemoveRange(0, n)` 은 남은 바이트를 매번 앞으로 당겨 복사해 O(n²) 가 된다.
    /// </summary>
    private sealed class ChannelState
    {
        private byte[] _buffer = new byte[4096];
        private int _start;
        private int _end;

        public byte? LastFrameCount { get; set; }

        public bool InSync { get; private set; }

        public int Count => _end - _start;

        /// <summary>아직 패킷으로 꺼내지 않은 바이트.</summary>
        public ReadOnlySpan<byte> Pending => _buffer.AsSpan(_start, _end - _start);

        public void Append(ReadOnlySpan<byte> data)
        {
            EnsureRoom(data.Length);
            data.CopyTo(_buffer.AsSpan(_end));
            _end += data.Length;
        }

        public void Consume(int length)
        {
            _start += length;
            if (_start == _end)
            {
                _start = 0;
                _end = 0;
            }
        }

        public void Desynchronize()
        {
            InSync = false;
            Clear();
        }

        public void Resynchronize()
        {
            InSync = true;
            Clear();
        }

        private void Clear()
        {
            _start = 0;
            _end = 0;
        }

        private void EnsureRoom(int length)
        {
            if (_end + length <= _buffer.Length)
            {
                return;
            }

            // 앞으로 당기기만 하면 되는 경우(_start > 0 인 채로 뒤가 모자란 경우)는 두지 않는다 —
            // Process 가 패킷이 시작되는 프레임마다 Resynchronize 로 버퍼를 비우므로, 소비가 일어나는
            // 동안 _start 가 0 보다 큰 채로 버퍼 끝까지 차는 흐름이 없다. 도달할 수 없는 가지를 남기면
            // 커버리지·뮤테이션이 영영 채워지지 않는다.
            int pending = Count;
            var bigger = new byte[Math.Max(_buffer.Length * 2, pending + length)];
            _buffer.AsSpan(_start, pending).CopyTo(bigger);
            _buffer = bigger;
            _start = 0;
            _end = pending;
        }
    }
}
