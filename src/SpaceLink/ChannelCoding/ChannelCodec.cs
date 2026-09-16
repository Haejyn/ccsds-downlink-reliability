namespace SpaceLink.ChannelCoding;

/// <summary>채널 복호 결과 — 전송 프레임을 꺼냈는지, 몇 심볼을 정정했는지.</summary>
public readonly record struct ChannelDecodeResult(bool Succeeded, int CorrectedSymbols, byte[]? TransferFrame);

/// <summary>
/// 채널 부호 계층 — 전송 프레임 하나를 CADU 로 만들고 되돌린다.
///
/// <code>
/// 보낼 때:  프레임 → (0 으로 채움) → RS 부호화 → PN 랜덤화 → 앞에 ASM 붙임
/// 받을 때:  ASM 떼어냄(FrameSynchronizer) → PN 되돌림 → RS 복호 → 프레임
/// </code>
///
/// 랜덤화를 RS **뒤에** 거는 것이 CCSDS 순서다 — 그래야 수신기가 랜덤화를 먼저 풀고 RS 로 정정할 수 있다.
/// </summary>
public sealed class ChannelCodec
{
    private readonly ReedSolomonCodec _reedSolomon;
    private readonly bool _randomize;

    public ChannelCodec(int transferFrameLength, int interleavingDepth = 1, bool randomize = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(transferFrameLength, 1);
        _reedSolomon = new ReedSolomonCodec(interleavingDepth);
        _randomize = randomize;
        if (transferFrameLength > _reedSolomon.DataLength)
        {
            throw new ArgumentOutOfRangeException(nameof(transferFrameLength), transferFrameLength,
                $"transfer frame must fit in {_reedSolomon.DataLength} bytes at interleaving depth {interleavingDepth}");
        }

        TransferFrameLength = transferFrameLength;
    }

    public int TransferFrameLength { get; }

    public int CodeblockLength => _reedSolomon.CodeblockLength;

    /// <summary>ASM 까지 포함한 전체 길이.</summary>
    public int CaduLength => FrameSynchronizer.MarkerLength + CodeblockLength;

    /// <summary>전송 프레임을 CADU 로 만든다 (ASM + 랜덤화된 RS 코드블록).</summary>
    public byte[] EncodeCadu(ReadOnlySpan<byte> transferFrame)
    {
        if (transferFrame.Length != TransferFrameLength)
        {
            throw new ArgumentException($"transfer frame must be {TransferFrameLength} bytes, got {transferFrame.Length}", nameof(transferFrame));
        }

        Span<byte> data = new byte[_reedSolomon.DataLength];
        transferFrame.CopyTo(data);
        byte[] codeblock = _reedSolomon.Encode(data);
        if (_randomize)
        {
            Pseudorandomizer.Apply(codeblock);
        }

        var cadu = new byte[CaduLength];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cadu, FrameSynchronizer.AttachedSyncMarker);
        codeblock.CopyTo(cadu.AsSpan(FrameSynchronizer.MarkerLength));
        return cadu;
    }

    /// <summary>코드블록(ASM 뗀 것)에서 전송 프레임을 꺼낸다.</summary>
    public ChannelDecodeResult DecodeCodeblock(ReadOnlySpan<byte> codeblock)
    {
        if (codeblock.Length != CodeblockLength)
        {
            throw new ArgumentException($"codeblock must be {CodeblockLength} bytes, got {codeblock.Length}", nameof(codeblock));
        }

        var working = codeblock.ToArray();
        if (_randomize)
        {
            Pseudorandomizer.Apply(working);
        }

        var data = new byte[_reedSolomon.DataLength];
        ReedSolomonResult result = _reedSolomon.Decode(working, data);
        return result.Succeeded
            ? new ChannelDecodeResult(true, result.CorrectedSymbols, data.AsSpan(0, TransferFrameLength).ToArray())
            : new ChannelDecodeResult(false, result.CorrectedSymbols, null);
    }
}
