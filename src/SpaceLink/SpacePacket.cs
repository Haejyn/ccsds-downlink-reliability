using System.Buffers.Binary;

namespace SpaceLink;

public enum PacketType
{
    Telemetry = 0,
    Telecommand = 1,
}

public enum SequenceFlags
{
    Continuation = 0b00,
    First = 0b01,
    Last = 0b10,
    Unsegmented = 0b11,
}

/// <summary>Space Packet 주 헤더 6 바이트를 해석한 값.</summary>
public readonly record struct PacketHeader(
    int Version,
    PacketType Type,
    bool HasSecondaryHeader,
    ushort Apid,
    SequenceFlags SequenceFlags,
    ushort SequenceCount,
    int DataLength)
{
    public int TotalLength => SpacePacket.PrimaryHeaderLength + DataLength;

    /// <summary>헤더 6 바이트를 읽는다. 길이가 모자라면 false.</summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out PacketHeader header)
    {
        if (bytes.Length < SpacePacket.PrimaryHeaderLength)
        {
            header = default;
            return false;
        }

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
        ushort len = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        header = new PacketHeader(
            Version: id >> 13,
            Type: (PacketType)((id >> 12) & 1),
            HasSecondaryHeader: ((id >> 11) & 1) == 1,
            Apid: (ushort)(id & SpacePacket.MaxApid),
            SequenceFlags: (SequenceFlags)(seq >> 14),
            SequenceCount: (ushort)(seq & SpacePacket.MaxSequenceCount),
            DataLength: len + 1);
        return true;
    }
}

/// <summary>CCSDS 133.0-B-2 Space Packet (버전 1, 주 헤더 6 바이트 + 데이터 필드 1~65536 바이트).</summary>
public sealed class SpacePacket : IEquatable<SpacePacket>
{
    public const int PrimaryHeaderLength = 6;
    public const ushort IdleApid = 0x7FF;
    public const int MaxApid = 0x7FF;
    public const int MaxSequenceCount = 0x3FFF;
    public const int MaxDataLength = 65536;

    private readonly byte[] _data;

    public SpacePacket(ushort apid, ushort sequenceCount, ReadOnlySpan<byte> data,
        SequenceFlags sequenceFlags = SequenceFlags.Unsegmented, PacketType type = PacketType.Telemetry,
        bool hasSecondaryHeader = false)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(apid, (ushort)MaxApid);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sequenceCount, (ushort)MaxSequenceCount);
        if (data.Length is < 1 or > MaxDataLength)
        {
            throw new ArgumentOutOfRangeException(nameof(data), data.Length, "packet data field must be 1..65536 bytes");
        }

        Apid = apid;
        SequenceCount = sequenceCount;
        SequenceFlags = sequenceFlags;
        Type = type;
        HasSecondaryHeader = hasSecondaryHeader;
        _data = data.ToArray();
    }

    public ushort Apid { get; }

    public ushort SequenceCount { get; }

    public SequenceFlags SequenceFlags { get; }

    public PacketType Type { get; }

    public bool HasSecondaryHeader { get; }

    public ReadOnlyMemory<byte> Data => _data;

    public int TotalLength => PrimaryHeaderLength + _data.Length;

    public bool IsIdle => Apid == IdleApid;

    public byte[] Encode()
    {
        var buffer = new byte[TotalLength];
        WriteHeader(buffer);
        _data.CopyTo(buffer, PrimaryHeaderLength);
        return buffer;
    }

    /// <summary>주 헤더 6 바이트를 쓴다. 부호화와 PEC 검증이 같은 배치를 쓰도록 한 곳에 둔다.</summary>
    private void WriteHeader(Span<byte> destination)
    {
        int id = ((int)Type << 12) | ((HasSecondaryHeader ? 1 : 0) << 11) | Apid;
        int seq = ((int)SequenceFlags << 14) | SequenceCount;
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)id);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)seq);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], (ushort)(_data.Length - 1));
    }

    /// <summary>바이트열 전체가 정확히 한 패킷이어야 한다. 버전·길이가 맞지 않으면 FormatException.</summary>
    public static SpacePacket Decode(ReadOnlySpan<byte> bytes)
    {
        if (!PacketHeader.TryRead(bytes, out PacketHeader header))
        {
            throw new FormatException($"packet shorter than primary header: {bytes.Length} bytes");
        }

        if (header.Version != 0)
        {
            throw new FormatException($"unsupported packet version number {header.Version}");
        }

        if (header.TotalLength != bytes.Length)
        {
            throw new FormatException($"packet length field says {header.TotalLength} bytes but {bytes.Length} given");
        }

        return new SpacePacket(header.Apid, header.SequenceCount, bytes[PrimaryHeaderLength..],
            header.SequenceFlags, header.Type, header.HasSecondaryHeader);
    }

    /// <summary>
    /// 데이터 필드 끝에 패킷 오류 제어(PEC, CRC-16/CCITT — 헤더부터 사용자 데이터 끝까지)를 붙인 패킷을 만든다.
    /// 프레임 계층이 감지하지 못하는 유실(8 비트 프레임 카운트가 한 바퀴 도는 256 장 단위 연속 유실)에서
    /// 잘못 이어 붙인 패킷을 수신 쪽이 걸러낼 수 있게 한다.
    /// </summary>
    public static SpacePacket WithErrorControl(ushort apid, ushort sequenceCount, ReadOnlySpan<byte> userData,
        SequenceFlags sequenceFlags = SequenceFlags.Unsegmented)
    {
        var data = new byte[userData.Length + 2];
        userData.CopyTo(data);
        var draft = new SpacePacket(apid, sequenceCount, data, sequenceFlags);
        byte[] encoded = draft.Encode();
        ushort crc = Crc16Ccitt.Compute(encoded.AsSpan(0, encoded.Length - 2));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(data.Length - 2), crc);
        return new SpacePacket(apid, sequenceCount, data, sequenceFlags);
    }

    /// <summary>데이터 필드 마지막 2 바이트가 패킷 전체(PEC 제외)의 CRC 와 일치하는가.</summary>
    public bool HasValidErrorControl()
    {
        if (_data.Length < 3)
        {
            return false;
        }

        // 패킷 전체를 다시 부호화하지 않는다 — 수신 처리기가 패킷마다 부르는 경로라
        // 사본 한 장이 곧 초당 수만 번의 할당이 된다. 헤더는 스택에, 데이터는 그대로 두고 이어서 계산한다.
        Span<byte> header = stackalloc byte[PrimaryHeaderLength];
        WriteHeader(header);
        ushort expected = Crc16Ccitt.Compute(_data.AsSpan(0, _data.Length - 2), Crc16Ccitt.Compute(header));
        return BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_data.Length - 2)) == expected;
    }

    public bool Equals(SpacePacket? other) =>
        other is not null
        && Apid == other.Apid
        && SequenceCount == other.SequenceCount
        && SequenceFlags == other.SequenceFlags
        && Type == other.Type
        && HasSecondaryHeader == other.HasSecondaryHeader
        && _data.AsSpan().SequenceEqual(other._data);

    public override bool Equals(object? obj) => Equals(obj as SpacePacket);

    public override int GetHashCode() => HashCode.Combine(Apid, SequenceCount, _data.Length);

    public override string ToString() => $"SpacePacket(apid={Apid}, seq={SequenceCount}, len={_data.Length})";
}
