using System.Globalization;
using SpaceLink.ChannelCoding;

namespace SpaceLink.Tests;

/// <summary>
/// 동기 마커 탐색 — 수신 비트열에는 바이트 경계도 프레임 경계도 없다.
/// 시험은 비트 슬립·마커 손상·잡음을 주입하고 **다시 잠기기까지 몇 코드블록이 걸리는지** 를 잰다.
/// </summary>
public class FrameSynchronizerTests
{
    private const int FrameLength = 128;

    private static ChannelCodec Codec() => new(FrameLength, interleavingDepth: 1, randomize: true);

    private static byte[] Frame(int index)
    {
        var frame = new byte[FrameLength];
        new Random(9000 + index).NextBytes(frame);
        return frame;
    }

    /// <summary>CADU 를 이어 붙인 스트림.</summary>
    private static byte[] Stream(ChannelCodec codec, int caduCount)
    {
        var stream = new List<byte>(codec.CaduLength * caduCount);
        for (int i = 0; i < caduCount; i++)
        {
            stream.AddRange(codec.EncodeCadu(Frame(i)));
        }

        return [.. stream];
    }

    /// <summary>비트 단위로 shift 만큼 밀어 바이트 경계를 어긋나게 만든다 (앞에 쓰레기 비트가 붙은 상황).</summary>
    private static byte[] ShiftBits(ReadOnlySpan<byte> data, int shift)
    {
        var shifted = new byte[data.Length + 1];
        for (int i = 0; i < data.Length; i++)
        {
            shifted[i] |= (byte)(data[i] >> shift);
            shifted[i + 1] = (byte)(data[i] << (8 - shift));
        }

        return shifted;
    }

    [Fact]
    [Trait("Requirement", "REQ-ASM-01")]
    public void Locks_onto_a_clean_stream_and_emits_every_codeblock()
    {
        ChannelCodec codec = Codec();
        var sync = new FrameSynchronizer(codec.CodeblockLength);
        List<byte[]> blocks = sync.Process(Stream(codec, 10));

        Assert.Equal(10, blocks.Count);
        Assert.Equal(SyncState.Lock, sync.State);
        Assert.Equal(0, sync.MarkersMissed);
        Assert.Equal(0, sync.Resyncs);

        // 내보낸 코드블록이 실제로 프레임으로 복호돼야 경계가 맞은 것이다.
        for (int i = 0; i < blocks.Count; i++)
        {
            ChannelDecodeResult decoded = codec.DecodeCodeblock(blocks[i]);
            Assert.True(decoded.Succeeded);
            Assert.Equal(Frame(i), decoded.TransferFrame);
        }
    }

    [Theory]
    [Trait("Requirement", "REQ-ASM-01")]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void Finds_the_marker_even_when_the_stream_is_not_byte_aligned(int shift)
    {
        // 바이트 정렬을 가정하면 여기서 한 장도 못 찾는다.
        ChannelCodec codec = Codec();
        var sync = new FrameSynchronizer(codec.CodeblockLength);
        List<byte[]> blocks = sync.Process(ShiftBits(Stream(codec, 6), shift));

        Assert.Equal(6, blocks.Count);
        Assert.Equal(SyncState.Lock, sync.State);
        for (int i = 0; i < blocks.Count; i++)
        {
            Assert.Equal(Frame(i), codec.DecodeCodeblock(blocks[i]).TransferFrame);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-ASM-02")]
    public void Resynchronizes_after_a_bit_slip_and_the_cost_is_measured()
    {
        // 중간에 비트가 끼어들면 그 뒤 경계가 전부 밀린다. 다시 잠글 때까지 몇 장을 잃는지 센다.
        ChannelCodec codec = Codec();
        byte[] head = Stream(codec, 5);
        byte[] tail = Stream(codec, 5);
        var slipped = new List<byte>(head);
        slipped.AddRange(ShiftBits(tail, 3));    // 3 비트 슬립

        var sync = new FrameSynchronizer(codec.CodeblockLength);
        List<byte[]> blocks = sync.Process([.. slipped]);

        int recovered = blocks.Count(b => codec.DecodeCodeblock(b).Succeeded);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"bit slip: 내보낸 {blocks.Count}장 · 복호 성공 {recovered}장 · 재동기 {sync.Resyncs}회 · 마커 놓침 {sync.MarkersMissed}회 · 최종 {sync.State}"));

        Assert.True(recovered >= 5, $"앞쪽 5장은 온전해야 한다 (실제 {recovered})");
        Assert.True(sync.Resyncs >= 1, "슬립 뒤에는 한 번은 다시 탐색해야 한다");
        Assert.Equal(SyncState.Lock, sync.State);

        // 실측(2026-09-16): 10 장 중 7 장 복원 · 재동기 1 회 · 마커 놓침 4 회.
        // 슬립 경계에 걸친 CADU 와 재탐색이 끝나기 전에 지나간 것은 원리적으로 잃는다.
        // 이 시험이 고정하는 것은 "복구한다" 가 아니라 **얼마를 잃고 복구하는가** 다.
        Assert.True(recovered >= 7, $"재동기 뒤 뒷부분이 살아나야 한다 (실제 {recovered})");
    }

    [Theory]
    [Trait("Requirement", "REQ-ASM-03")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Damaged_markers_within_tolerance_keep_the_lock(int flippedBits)
    {
        // 마커가 조금 망가져도 (해밍 거리 ≤ 허용치) 경계는 그대로 믿는다.
        ChannelCodec codec = Codec();
        byte[] stream = Stream(codec, 8);
        int caduBits = codec.CaduLength * 8;
        for (int bit = 0; bit < flippedBits; bit++)
        {
            int bitIndex = (3 * caduBits) + bit;     // 넷째 CADU 의 마커 앞부분
            stream[bitIndex / 8] ^= (byte)(0x80 >> (bitIndex % 8));
        }

        var sync = new FrameSynchronizer(codec.CodeblockLength, maxMarkerBitErrors: 3);
        List<byte[]> blocks = sync.Process(stream);

        Assert.Equal(8, blocks.Count);
        Assert.Equal(SyncState.Lock, sync.State);
        Assert.Equal(0, sync.MarkersMissed);
        Assert.Equal(0, sync.Resyncs);
    }

    [Fact]
    [Trait("Requirement", "REQ-ASM-03")]
    public void Flywheel_keeps_emitting_through_missing_markers_then_gives_up()
    {
        // 허용치를 넘는 손상이 연속으로 오면 flywheel 로 버티다가 결국 다시 탐색으로 돌아간다.
        ChannelCodec codec = Codec();
        byte[] stream = Stream(codec, 12);
        int caduBits = codec.CaduLength * 8;
        for (int cadu = 3; cadu <= 8; cadu++)
        {
            for (int bit = 0; bit < 12; bit++)          // 마커를 알아볼 수 없게 뭉갠다
            {
                int bitIndex = (cadu * caduBits) + bit;
                stream[bitIndex / 8] ^= (byte)(0x80 >> (bitIndex % 8));
            }
        }

        var sync = new FrameSynchronizer(codec.CodeblockLength, flywheelTolerance: 3);
        List<byte[]> blocks = sync.Process(stream);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"flywheel: 내보낸 {blocks.Count}장 · 마커 놓침 {sync.MarkersMissed}회 · 재동기 {sync.Resyncs}회 · 최종 {sync.State}"));

        Assert.True(sync.MarkersMissed >= 3, "flywheel 로 버틴 구간이 있어야 한다");
        Assert.True(sync.Resyncs >= 1, "허용치를 넘으면 탐색으로 돌아가야 한다");
        Assert.True(blocks.Count >= 3, "잠금 구간에서는 내보냈어야 한다");
    }

    [Fact]
    [Trait("Requirement", "REQ-ASM-01")]
    public void Partial_stream_waits_for_more_data_instead_of_guessing()
    {
        ChannelCodec codec = Codec();
        byte[] stream = Stream(codec, 3);
        var sync = new FrameSynchronizer(codec.CodeblockLength);

        // 절반만 넣으면 아직 완성되지 않은 코드블록을 내보내면 안 된다.
        List<byte[]> first = sync.Process(stream.AsSpan(0, codec.CaduLength + 10));
        Assert.Single(first);

        List<byte[]> rest = sync.Process(stream.AsSpan(codec.CaduLength + 10));
        Assert.Equal(2, rest.Count);
        Assert.Equal(3, sync.CodeblocksEmitted);
    }

    [Fact]
    [Trait("Requirement", "REQ-ASM-01")]
    public void Pure_noise_never_produces_a_codeblock()
    {
        // 마커가 없는 잡음에서 경계를 지어내면 안 된다 — 아무것도 내보내지 않고 계속 찾는다.
        ChannelCodec codec = Codec();
        var sync = new FrameSynchronizer(codec.CodeblockLength, maxMarkerBitErrors: 0);
        var noise = new byte[codec.CaduLength * 4];
        new Random(777).NextBytes(noise);

        Assert.Empty(sync.Process(noise));
        Assert.Equal(SyncState.Search, sync.State);
        Assert.Equal(0, sync.CodeblocksEmitted);
    }

    [Fact]
    [Trait("Requirement", "REQ-ASM-03")]
    public void A_false_marker_is_dropped_when_the_next_one_does_not_follow()
    {
        // 잡음 속에 마커처럼 생긴 32 비트가 하나 있다고 바로 잠기면 안 된다.
        // 다음 경계에 마커가 없으면 후보를 버리고 그 다음 비트부터 다시 찾아야 한다.
        ChannelCodec codec = Codec();
        var prefix = new byte[codec.CaduLength / 2];      // 진짜 경계와 어긋나게 짧게 둔다
        new Random(4242).NextBytes(prefix);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(prefix, FrameSynchronizer.AttachedSyncMarker);

        var stream = new List<byte>(prefix);
        stream.AddRange(Stream(codec, 4));

        var sync = new FrameSynchronizer(codec.CodeblockLength, maxMarkerBitErrors: 0);
        List<byte[]> blocks = sync.Process([.. stream]);

        // 가짜 마커에 걸린 한 장은 내보내고(뒷단 RS·CRC 가 거른다), 후보를 버린 뒤 진짜 흐름을 전부 잡아야 한다.
        Assert.Equal(SyncState.Lock, sync.State);
        Assert.False(codec.DecodeCodeblock(blocks[0]).Succeeded, "첫 장은 가짜 경계라 복호에 실패해야 한다");

        int decodable = blocks.Count(b => codec.DecodeCodeblock(b).Succeeded);
        Assert.True(decodable >= 4, $"가짜 마커를 버린 뒤 진짜 CADU 4 장을 모두 잡아야 한다 (실제 {decodable}/{blocks.Count})");
    }

    [Fact]
    [Trait("Requirement", "REQ-ASM-01")]
    public void A_lock_threshold_of_one_locks_on_the_first_marker()
    {
        ChannelCodec codec = Codec();
        var sync = new FrameSynchronizer(codec.CodeblockLength, lockThreshold: 1);
        List<byte[]> blocks = sync.Process(Stream(codec, 3));

        Assert.Equal(SyncState.Lock, sync.State);
        Assert.Equal(3, blocks.Count);
    }

    [Fact]
    [Trait("Requirement", "REQ-ASM-01")]
    public void Invalid_construction_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameSynchronizer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameSynchronizer(255, lockThreshold: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameSynchronizer(255, flywheelTolerance: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameSynchronizer(255, maxMarkerBitErrors: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelCodec(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelCodec(100_000));
        Assert.Throws<ArgumentException>(() => Codec().EncodeCadu(new byte[10]));
        Assert.Throws<ArgumentException>(() => Codec().DecodeCodeblock(new byte[10]));
    }
}
