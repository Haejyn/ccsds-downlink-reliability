namespace SpaceLink.ChannelCoding;

/// <summary>채널 복호 결과 — 전송 프레임을 꺼냈는지, 몇 심볼을 정정했는지.</summary>
public readonly record struct ChannelDecodeResult(bool Succeeded, int CorrectedSymbols, byte[]? TransferFrame);

/// <summary>
/// 채널 부호 계층 — 전송 프레임 하나를 CADU 로 만들고 되돌린다.
///
/// <code>
/// 보낼 때:  프레임 → RS 부호화(앞쪽 가상 채움은 계산에만) → PN 랜덤화 → 앞에 ASM 붙임
/// 받을 때:  ASM 떼어냄(FrameSynchronizer) → PN 되돌림 → RS 복호(가상 채움을 0 으로 되살려) → 프레임
/// </code>
///
/// 랜덤화를 RS **뒤에** 거는 것이 CCSDS 순서다 — 그래야 수신기가 랜덤화를 먼저 풀고 RS 로 정정할 수 있다.
///
/// 프레임이 223·I 바이트보다 짧으면 **짧은 코드블록**을 쓴다(§4.3.7) — 모자란 만큼을 코드블록 앞쪽의 가상 채움으로
/// 계산하고 보내지 않는다. 예전에는 프레임 뒤를 0 으로 채워 255 바이트를 전부 보냈다: 128 바이트 프레임이면
/// CADU 가 259 바이트였고(표준은 164), 채움 자리가 달라 패리티도 표준 부호기와 달랐다.
/// </summary>
public sealed class ChannelCodec
{
    private readonly ReedSolomonCodec _reedSolomon;
    private readonly bool _randomize;
    private readonly PseudorandomSequence _sequence;

    /// <param name="sequence">PN 수열 — 기본은 표준의 131071 비트(§10.4.1). 255 비트는 옛 시스템 호환용이다(§10.4.2).</param>
    public ChannelCodec(int transferFrameLength, int interleavingDepth = 1, bool randomize = true,
        PseudorandomSequence sequence = PseudorandomSequence.Standard131071)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(transferFrameLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(interleavingDepth, 1);
        int capacity = ReedSolomonCodec.DataSymbolsPerCodeword * interleavingDepth;
        if (transferFrameLength > capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(transferFrameLength), transferFrameLength,
                $"transfer frame must fit in {capacity} bytes at interleaving depth {interleavingDepth}");
        }

        // 가상 채움 Q 는 I 의 배수여야 한다(§4.3.7.3) — 곧 프레임 길이도 I 의 배수여야 한다.
        int virtualFill = capacity - transferFrameLength;
        if (virtualFill % interleavingDepth != 0)
        {
            throw new ArgumentException(
                $"transfer frame length ({transferFrameLength}) must be a multiple of the interleaving depth ({interleavingDepth}) " +
                "so that the virtual fill is too", nameof(transferFrameLength));
        }

        _reedSolomon = new ReedSolomonCodec(interleavingDepth, virtualFill);
        _randomize = randomize;
        _sequence = sequence;
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

        byte[] codeblock = _reedSolomon.Encode(transferFrame);
        if (_randomize)
        {
            Pseudorandomizer.Apply(codeblock, _sequence);
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
            Pseudorandomizer.Apply(working, _sequence);
        }

        var frame = new byte[TransferFrameLength];
        ReedSolomonResult result = _reedSolomon.Decode(working, frame);
        return result.Succeeded
            ? new ChannelDecodeResult(true, result.CorrectedSymbols, frame)
            : new ChannelDecodeResult(false, result.CorrectedSymbols, null);
    }
}
