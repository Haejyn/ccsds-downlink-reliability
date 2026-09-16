using System.Buffers.Binary;

namespace SpaceLink;

/// <summary>
/// 임무별로 고정되는 TM 전송 프레임 형식 (CCSDS 132.0-B). 패킷 모드라 제1 헤더 포인터(11 비트)가
/// 데이터 필드 안의 위치를 가리킬 수 있어야 하므로 데이터 필드는 최대 2046 바이트로 제한한다.
/// </summary>
public sealed record FrameConfig
{
    public const int PrimaryHeaderLength = 6;
    public const int OperationalControlFieldLength = 4;
    public const int FrameErrorControlFieldLength = 2;
    public const int MaxDataFieldLength = 2046;

    public FrameConfig(int frameLength, bool hasOperationalControlField = false, bool hasFrameErrorControl = true)
    {
        FrameLength = frameLength;
        HasOperationalControlField = hasOperationalControlField;
        HasFrameErrorControl = hasFrameErrorControl;
        if (DataFieldLength is < SpacePacket.PrimaryHeaderLength + 1 or > MaxDataFieldLength)
        {
            throw new ArgumentOutOfRangeException(nameof(frameLength), frameLength,
                $"data field must be {SpacePacket.PrimaryHeaderLength + 1}..{MaxDataFieldLength} bytes");
        }
    }

    public int FrameLength { get; }

    public bool HasOperationalControlField { get; }

    public bool HasFrameErrorControl { get; }

    public int DataFieldLength =>
        FrameLength - PrimaryHeaderLength
        - (HasOperationalControlField ? OperationalControlFieldLength : 0)
        - (HasFrameErrorControl ? FrameErrorControlFieldLength : 0);
}

public static class FirstHeaderPointer
{
    /// <summary>이 프레임 안에서 시작하는 패킷이 없다 (앞 패킷의 연속 데이터만 있음).</summary>
    public const ushort NoPacketStart = 0x7FF;

    /// <summary>유휴 데이터만 담은 프레임 (OID).</summary>
    public const ushort IdleData = 0x7FE;
}

public enum FrameError
{
    None,
    WrongLength,
    UnsupportedVersion,
    CrcMismatch,
    InvalidDataFieldStatus,
}

public readonly record struct FrameDecodeResult(TransferFrame? Frame, FrameError Error)
{
    public bool IsValid => Error == FrameError.None;
}

/// <summary>TM 전송 프레임 (버전 1, 부 헤더 없음, 동기화 플래그 0 = 패킷 모드).</summary>
public sealed class TransferFrame
{
    public const int MaxSpacecraftId = 0x3FF;
    public const int MaxVirtualChannelId = 7;

    private readonly byte[] _dataField;

    public TransferFrame(ushort spacecraftId, byte virtualChannelId, byte masterChannelFrameCount,
        byte virtualChannelFrameCount, ushort firstHeaderPointer, ReadOnlySpan<byte> dataField,
        uint operationalControlField = 0)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(spacecraftId, (ushort)MaxSpacecraftId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(virtualChannelId, (byte)MaxVirtualChannelId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstHeaderPointer, FirstHeaderPointer.NoPacketStart);
        SpacecraftId = spacecraftId;
        VirtualChannelId = virtualChannelId;
        MasterChannelFrameCount = masterChannelFrameCount;
        VirtualChannelFrameCount = virtualChannelFrameCount;
        FirstHeaderPointerValue = firstHeaderPointer;
        OperationalControlField = operationalControlField;
        _dataField = dataField.ToArray();
    }

    public ushort SpacecraftId { get; }

    public byte VirtualChannelId { get; }

    public byte MasterChannelFrameCount { get; }

    public byte VirtualChannelFrameCount { get; }

    public ushort FirstHeaderPointerValue { get; }

    public uint OperationalControlField { get; }

    public ReadOnlyMemory<byte> DataField => _dataField;

    public byte[] Encode(FrameConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_dataField.Length != config.DataFieldLength)
        {
            throw new InvalidOperationException(
                $"data field is {_dataField.Length} bytes but the frame format needs {config.DataFieldLength}");
        }

        if (FirstHeaderPointerValue < FirstHeaderPointer.IdleData && FirstHeaderPointerValue >= config.DataFieldLength)
        {
            throw new InvalidOperationException($"first header pointer {FirstHeaderPointerValue} is outside the data field");
        }

        var frame = new byte[config.FrameLength];
        int id = (SpacecraftId << 4) | (VirtualChannelId << 1) | (config.HasOperationalControlField ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)id);
        frame[2] = MasterChannelFrameCount;
        frame[3] = VirtualChannelFrameCount;
        const int segmentLengthIdUnsegmented = 0b11 << 11;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(segmentLengthIdUnsegmented | FirstHeaderPointerValue));
        _dataField.CopyTo(frame, FrameConfig.PrimaryHeaderLength);
        int offset = FrameConfig.PrimaryHeaderLength + _dataField.Length;
        if (config.HasOperationalControlField)
        {
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(offset), OperationalControlField);
            offset += FrameConfig.OperationalControlFieldLength;
        }

        if (config.HasFrameErrorControl)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset), Crc16Ccitt.Compute(frame.AsSpan(0, offset)));
        }

        return frame;
    }

    /// <summary>프레임을 검증하고 해석한다. 예외를 던지지 않고 오류 종류를 돌려준다.</summary>
    public static FrameDecodeResult Decode(ReadOnlySpan<byte> bytes, FrameConfig config)
    {
        FrameError error = Validate(bytes, config, out FrameView view);
        if (error != FrameError.None)
        {
            return new FrameDecodeResult(null, error);
        }

        var frame = new TransferFrame(
            spacecraftId: view.SpacecraftId,
            virtualChannelId: view.VirtualChannelId,
            masterChannelFrameCount: view.MasterChannelFrameCount,
            virtualChannelFrameCount: view.VirtualChannelFrameCount,
            firstHeaderPointer: view.FirstHeaderPointerValue,
            dataField: view.DataField,
            operationalControlField: view.OperationalControlField);
        return new FrameDecodeResult(frame, FrameError.None);
    }

    /// <summary>
    /// 검증만 하고 필요한 필드를 뷰로 돌려준다 — 프레임 객체도 데이터 사본도 만들지 않는다.
    /// 수신 처리기가 프레임마다 부르는 경로라, 여기서 생기는 할당 한 번이 곧 초당 수만 번이 된다.
    /// <see cref="Decode"/> 도 이 검증을 그대로 쓴다 (규칙이 두 벌로 갈라지지 않게).
    /// </summary>
    internal static FrameError Validate(ReadOnlySpan<byte> bytes, FrameConfig config, out FrameView view)
    {
        ArgumentNullException.ThrowIfNull(config);
        view = default;
        if (bytes.Length != config.FrameLength)
        {
            return FrameError.WrongLength;
        }

        if (config.HasFrameErrorControl)
        {
            int crcOffset = bytes.Length - FrameConfig.FrameErrorControlFieldLength;
            if (Crc16Ccitt.Compute(bytes[..crcOffset]) != BinaryPrimitives.ReadUInt16BigEndian(bytes[crcOffset..]))
            {
                return FrameError.CrcMismatch;
            }
        }

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        if (id >> 14 != 0)
        {
            return FrameError.UnsupportedVersion;
        }

        bool ocfFlag = (id & 1) == 1;
        ushort status = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        bool secondaryHeader = (status & 0x8000) != 0;
        bool syncFlag = (status & 0x4000) != 0;
        int segmentLengthId = (status >> 11) & 0b11;
        ushort fhp = (ushort)(status & 0x7FF);
        bool pointerInside = fhp < config.DataFieldLength || fhp is FirstHeaderPointer.IdleData or FirstHeaderPointer.NoPacketStart;
        if (ocfFlag != config.HasOperationalControlField || secondaryHeader || syncFlag || segmentLengthId != 0b11 || !pointerInside)
        {
            return FrameError.InvalidDataFieldStatus;
        }

        int dataEnd = FrameConfig.PrimaryHeaderLength + config.DataFieldLength;
        uint ocf = config.HasOperationalControlField ? BinaryPrimitives.ReadUInt32BigEndian(bytes[dataEnd..]) : 0;
        view = new FrameView(
            spacecraftId: (ushort)(id >> 4),
            virtualChannelId: (byte)((id >> 1) & 0x7),
            masterChannelFrameCount: bytes[2],
            virtualChannelFrameCount: bytes[3],
            firstHeaderPointer: fhp,
            dataField: bytes[FrameConfig.PrimaryHeaderLength..dataEnd],
            operationalControlField: ocf);
        return FrameError.None;
    }
}

/// <summary>검증된 프레임에서 수신 처리에 필요한 값만 담은 뷰. 데이터 필드는 원본 버퍼를 그대로 가리킨다.</summary>
internal readonly ref struct FrameView
{
    public FrameView(ushort spacecraftId, byte virtualChannelId, byte masterChannelFrameCount,
        byte virtualChannelFrameCount, ushort firstHeaderPointer, ReadOnlySpan<byte> dataField,
        uint operationalControlField)
    {
        SpacecraftId = spacecraftId;
        VirtualChannelId = virtualChannelId;
        MasterChannelFrameCount = masterChannelFrameCount;
        VirtualChannelFrameCount = virtualChannelFrameCount;
        FirstHeaderPointerValue = firstHeaderPointer;
        DataField = dataField;
        OperationalControlField = operationalControlField;
    }

    public ushort SpacecraftId { get; }

    public byte VirtualChannelId { get; }

    public byte MasterChannelFrameCount { get; }

    public byte VirtualChannelFrameCount { get; }

    public ushort FirstHeaderPointerValue { get; }

    public ReadOnlySpan<byte> DataField { get; }

    public uint OperationalControlField { get; }
}
