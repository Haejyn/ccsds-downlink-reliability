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

    /// <summary>first..last 번째 CADU 의 마커 앞 12 비트를 뭉갠다 — 해밍 거리 12 는 어떤 허용치(≤ 3)로도 알아볼 수 없다.</summary>
    private static void DamageMarkers(byte[] stream, ChannelCodec codec, int first, int last)
    {
        int caduBits = codec.CaduLength * 8;
        for (int cadu = first; cadu <= last; cadu++)
        {
            for (int bit = 0; bit < 12; bit++)
            {
                int bitIndex = (cadu * caduBits) + bit;
                stream[bitIndex / 8] ^= (byte)(0x80 >> (bitIndex % 8));
            }
        }
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

    [Fact]
    [Trait("Requirement", "REQ-RS-01")]
    public void Frame_exactly_at_the_data_capacity_is_accepted_one_byte_over_is_refused()
    {
        // 생성자는 `transferFrameLength > DataLength` 로 거부한다 — 경계값(= DataLength)은 받아들여야 맞다.
        // `>` 를 `>=` 로 바꾸는 뮤턴트는 정확히 223 바이트(인터리빙 1의 데이터 용량)를 잘못 거부하게 되는데,
        // 지금까지는 0 바이트·100,000 바이트만 시험해서 그 경계 자체를 아무도 짚지 않았다.
        int dataLength = new ReedSolomonCodec(interleavingDepth: 1).DataLength;
        Assert.Equal(223, dataLength);

        var atCapacity = new ChannelCodec(dataLength);
        Assert.Equal(dataLength, atCapacity.TransferFrameLength);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelCodec(dataLength + 1));
        Assert.Equal(dataLength + 1, ex.ActualValue);
    }

    // ───── 버퍼가 유계인가, 버린 경계에서 내보내지 않는가, 허용치의 경계 ─────
    // 출처: Stryker 생존 중 이 클래스의 `Trim()` 삭제(137·140·166) · `dropBytes <= 0`(182·184) ·
    //       `continue` 삭제(130) · 허용치 `>` ↔ `>=`(123).
    // 기존 시험이 못 잡은 이유 —
    //  · 버퍼 정리는 `Process` 의 반환값을 바꾸지 않아 메모리에만 나타난다. 게다가 `Trim()` 이 세 곳에서 중복으로
    //    불려 하나를 지워도 나머지가 덮어 주었다. 중복 둘을 제품 코드에서 지운 뒤에야 남은 하나가 하중을 받는다.
    //  · 재동기 시험은 **복호에 성공한 장수**만 셌다. 버린 경계에서 코드블록이 하나 새어 나가도 그 수는 그대로다.
    //  · flywheel 시험은 손상 마커를 6 연속으로 넣고 `>=` 로만 단언해 허용치 경계의 바로 위·아래를 구분하지 못했다.

    [Fact]
    [Trait("Requirement", "REQ-ASM-04")]
    public void Buffered_bytes_stay_within_two_cadus_however_long_the_stream_is()
    {
        // 스트림 전체를 한 번에 넣으면 Process 호출 안의 정리는 보이지 않는다 — 실제 수신처럼 조각으로 넣고
        // 매 호출이 **반환된 뒤**의 보유량을 본다. 조각(1,000 B)을 CADU(259 B)와 나누어떨어지지 않게 둬서
        // 매 호출이 코드블록 한가운데에서 끝나게 한다 — 잘라낼 자리를 잘못 잡는 구현이면 버퍼가 스트림 길이만큼 자란다.
        // 상한의 근거는 구현이 아니라 알고리즘이다: 잠근 뒤에는 CADU 한 장이 다 찰 때까지만 들고 있으면 되고
        // (그 전에는 Process 가 멈춘다) 그 이상 보관할 이유가 없다. 두 장은 조각 경계 여유다.
        ChannelCodec codec = Codec();
        const int caduCount = 4_000;      // 약 1 MB
        const int chunkBytes = 1_000;
        byte[] stream = Stream(codec, caduCount);
        var sync = new FrameSynchronizer(codec.CodeblockLength);

        int peak = 0;
        for (int offset = 0; offset < stream.Length; offset += chunkBytes)
        {
            sync.Process(stream.AsSpan(offset, Math.Min(chunkBytes, stream.Length - offset)));
            peak = Math.Max(peak, sync.BufferedBytes);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"보유 바이트: 스트림 {stream.Length:N0} B · 최대 {peak} B (CADU 한 장 {codec.CaduLength} B)"));

        Assert.Equal(caduCount, sync.CodeblocksEmitted);   // 상한을 지키려고 데이터를 버린 게 아님을 함께 확인
        Assert.True(peak <= 2 * codec.CaduLength,
            $"보유 바이트가 CADU 두 장({2 * codec.CaduLength} B)을 넘었다 — 스트림 길이에 따라 자란다 (최대 {peak} B)");
    }

    [Fact]
    [Trait("Requirement", "REQ-ASM-04")]
    public void While_searching_only_the_last_bits_a_marker_could_still_start_in_are_kept()
    {
        // 마커가 없는 잡음. 허용치 0 으로 잡아 우연히 마커처럼 보이는 32 비트가 없게 한다(고정 시드).
        // 탐색 중에는 마커가 아직 시작할 수 있는 마지막 31 비트만 있으면 된다. 바이트 단위로만 버릴 수 있어
        // 정렬 여유 7 비트를 더한 38 비트 = 5 바이트가 상한이다.
        ChannelCodec codec = Codec();
        var sync = new FrameSynchronizer(codec.CodeblockLength, maxMarkerBitErrors: 0);
        var noise = new byte[256 * 1024];
        new Random(2026).NextBytes(noise);

        int peak = 0;
        for (int offset = 0; offset < noise.Length; offset += 1_000)
        {
            sync.Process(noise.AsSpan(offset, Math.Min(1_000, noise.Length - offset)));
            peak = Math.Max(peak, sync.BufferedBytes);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"탐색 중 보유 바이트: 잡음 {noise.Length:N0} B · 최대 {peak} B"));

        Assert.Equal(SyncState.Search, sync.State);
        Assert.Equal(0, sync.CodeblocksEmitted);
        Assert.True(peak <= 5, $"탐색 중에는 마커 32 비트에 한 비트 모자란 만큼만 들고 있으면 된다 (최대 {peak} B)");
    }

    [Fact]
    [Trait("Requirement", "REQ-ASM-03")]
    public void Giving_up_on_a_boundary_drops_the_codeblock_there_instead_of_emitting_it()
    {
        // 허용치 3 에서 마커를 4 장 연속(CADU 5~8)으로 뭉갠다. 앞의 셋(5·6·7)은 관성으로 내보낸다 — 경계가 아직 맞을 수 있다.
        // 넷째(8)에서 경계를 **버리기로 판정**하므로 그 자리의 코드블록은 내보내면 안 된다. 버린 경계에서 뽑은 데이터는 믿지 않는다.
        // 손 계산: 0~4 (5 장) + flywheel 5·6·7 (3 장) + 재탐색 뒤 9~11 (3 장) = 11 장 · 재동기 1 회 · 마커 놓침 4 회.
        // 뭉갠 것은 마커뿐이라 8번 코드블록 자체는 온전하다 — 그래서 새어 나가면 "복호 성공 수" 로는 드러나지 않는다.
        ChannelCodec codec = Codec();
        byte[] stream = Stream(codec, 12);
        DamageMarkers(stream, codec, first: 5, last: 8);

        var sync = new FrameSynchronizer(codec.CodeblockLength, flywheelTolerance: 3);
        List<byte[]> blocks = sync.Process(stream);

        Assert.Equal(1, sync.Resyncs);
        Assert.Equal(4, sync.MarkersMissed);
        Assert.True(blocks.Count == 11,
            $"0~4 · 5~7(flywheel) · 9~11(재탐색 뒤) = 11 장이어야 한다 — 버린 경계(8)의 코드블록을 내보내면 12 장이 된다 (실제 {blocks.Count})");

        int[] expectedFrames = [0, 1, 2, 3, 4, 5, 6, 7, 9, 10, 11];
        for (int i = 0; i < blocks.Count; i++)
        {
            Assert.True(Frame(expectedFrames[i]).AsSpan().SequenceEqual(codec.DecodeCodeblock(blocks[i]).TransferFrame),
                $"{i} 번째로 나온 코드블록은 프레임 {expectedFrames[i]} 여야 한다");
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-ASM-03")]
    public void Missing_exactly_the_tolerated_number_of_markers_keeps_the_boundary()
    {
        // 허용치와 **같은** 수(3)만큼 연속으로 놓치는 건 아직 버틴다 — 넘어야(네 번째) 탐색으로 돌아간다.
        // 다섯째 CADU 부터 셋(5·6·7)을 뭉갠 뒤 8번 마커가 살아 있으면 경계를 그대로 믿고 잠금으로 돌아와야 한다.
        ChannelCodec codec = Codec();
        byte[] stream = Stream(codec, 12);
        DamageMarkers(stream, codec, first: 5, last: 7);

        var sync = new FrameSynchronizer(codec.CodeblockLength, flywheelTolerance: 3);
        List<byte[]> blocks = sync.Process(stream);

        Assert.Equal(0, sync.Resyncs);
        Assert.Equal(3, sync.MarkersMissed);
        Assert.Equal(SyncState.Lock, sync.State);
        Assert.True(blocks.Count == 12, $"경계를 한 번도 잃지 않았으니 12 장 모두 나와야 한다 (실제 {blocks.Count})");
        for (int i = 0; i < blocks.Count; i++)
        {
            Assert.True(Frame(i).AsSpan().SequenceEqual(codec.DecodeCodeblock(blocks[i]).TransferFrame),
                $"{i} 번째 코드블록은 프레임 {i} 여야 한다");
        }
    }
}
