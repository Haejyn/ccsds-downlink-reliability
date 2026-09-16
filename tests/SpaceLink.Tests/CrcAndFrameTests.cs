using System.Buffers.Binary;

namespace SpaceLink.Tests;

public class CrcTests
{
    [Fact]
    [Trait("Requirement", "REQ-CRC-01")]
    public void Check_value_of_standard_test_string_is_0x29B1()
    {
        Assert.Equal(0x29B1, Crc16Ccitt.Compute("123456789"u8));
        Assert.Equal(0xFFFF, Crc16Ccitt.Compute([]));
    }

    [Fact]
    [Trait("Requirement", "REQ-CRC-01")]
    public void Table_driven_crc_matches_bitwise_reference_on_random_buffers()
    {
        var rnd = new Random(20260915);
        for (int i = 0; i < 3_000; i++)
        {
            var buffer = new byte[rnd.Next(0, 400)];
            rnd.NextBytes(buffer);
            Assert.Equal(Downlink.ReferenceCrc(buffer), Crc16Ccitt.Compute(buffer));
        }
    }
}

public class FrameErrorDetectionTests
{
    // 실제 임무에서 흔한 1115 바이트 프레임 (데이터 1107 + FECF)
    private static readonly FrameConfig Mission = new(frameLength: 1115);

    private static byte[] ValidFrame(int seed)
    {
        var data = new byte[Mission.DataFieldLength];
        new Random(seed).NextBytes(data);
        return new TransferFrame(0x155, 3, 7, 7, 0, data).Encode(Mission);
    }

    private static void AssertRejected(byte[] frame, string what) =>
        Assert.False(TransferFrame.Decode(frame, Mission).IsValid, what);

    [Fact]
    [Trait("Requirement", "REQ-FRM-02")]
    public void Every_single_bit_error_in_a_1115_byte_frame_is_detected()
    {
        byte[] frame = ValidFrame(1);
        Assert.True(TransferFrame.Decode(frame, Mission).IsValid);
        for (int bit = 0; bit < frame.Length * 8; bit++)
        {
            byte[] corrupted = (byte[])frame.Clone();
            corrupted[bit / 8] ^= (byte)(0x80 >> (bit % 8));
            AssertRejected(corrupted, $"bit {bit}");
        }
    }

    [Theory]
    [Trait("Requirement", "REQ-FRM-02")]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void Random_multi_bit_errors_are_detected(int bitErrors)
    {
        var rnd = new Random(bitErrors);
        byte[] frame = ValidFrame(2);
        for (int trial = 0; trial < 20_000; trial++)
        {
            byte[] corrupted = (byte[])frame.Clone();
            var positions = new HashSet<int>();
            while (positions.Count < bitErrors)
            {
                positions.Add(rnd.Next(frame.Length * 8));
            }

            foreach (int bit in positions)
            {
                corrupted[bit / 8] ^= (byte)(0x80 >> (bit % 8));
            }

            AssertRejected(corrupted, $"bits {string.Join(',', positions)}");
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-FRM-02")]
    public void Every_burst_error_up_to_16_bits_is_detected()
    {
        var rnd = new Random(16);
        byte[] frame = ValidFrame(3);
        for (int trial = 0; trial < 20_000; trial++)
        {
            int length = rnd.Next(1, 17);
            int start = rnd.Next(frame.Length * 8 - length + 1);
            byte[] corrupted = (byte[])frame.Clone();
            for (int k = 0; k < length; k++)
            {
                bool flip = k == 0 || k == length - 1 || rnd.Next(2) == 1;   // 버스트 정의: 양 끝 비트는 반드시 오류
                if (flip)
                {
                    int bit = start + k;
                    corrupted[bit / 8] ^= (byte)(0x80 >> (bit % 8));
                }
            }

            AssertRejected(corrupted, $"burst start={start} len={length}");
        }
    }
}

public class TransferFrameTests
{
    private static readonly FrameConfig Small = new(frameLength: 16);   // 헤더 6 + 데이터 8 + FECF 2

    [Fact]
    [Trait("Requirement", "REQ-FRM-01")]
    public void Encoding_follows_the_ccsds_bit_layout()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] frame = new TransferFrame(spacecraftId: 0x155, virtualChannelId: 5, masterChannelFrameCount: 0x12,
            virtualChannelFrameCount: 0x34, firstHeaderPointer: 3, data).Encode(Small);
        // 버전 00 | SCID 0101010101 | VCID 101 | OCF 0  →  0x155A
        // 데이터 필드 상태: 부헤더 0 | 동기 0 | 순서 0 | 세그먼트 길이 11 | FHP 00000000011  →  0x1803
        byte[] header = [0x15, 0x5A, 0x12, 0x34, 0x18, 0x03];
        Assert.Equal(header, frame[..6]);
        Assert.Equal(data, frame[6..14]);
        Assert.Equal(Downlink.ReferenceCrc(frame.AsSpan(0, 14)), BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14)));
    }

    [Fact]
    [Trait("Requirement", "REQ-FRM-01")]
    public void Random_frames_round_trip_with_and_without_ocf()
    {
        var rnd = new Random(7);
        foreach (FrameConfig config in new[] { new FrameConfig(200), new FrameConfig(200, hasOperationalControlField: true) })
        {
            for (int i = 0; i < 2_000; i++)
            {
                var data = new byte[config.DataFieldLength];
                rnd.NextBytes(data);
                ushort fhp = rnd.Next(3) switch
                {
                    0 => FirstHeaderPointer.NoPacketStart,
                    1 => FirstHeaderPointer.IdleData,
                    _ => (ushort)rnd.Next(config.DataFieldLength),
                };
                var original = new TransferFrame((ushort)rnd.Next(1024), (byte)rnd.Next(8), (byte)rnd.Next(256),
                    (byte)rnd.Next(256), fhp, data, config.HasOperationalControlField ? (uint)rnd.Next() : 0);
                FrameDecodeResult decoded = TransferFrame.Decode(original.Encode(config), config);
                Assert.True(decoded.IsValid);
                TransferFrame f = decoded.Frame!;
                Assert.Equal(original.SpacecraftId, f.SpacecraftId);
                Assert.Equal(original.VirtualChannelId, f.VirtualChannelId);
                Assert.Equal(original.MasterChannelFrameCount, f.MasterChannelFrameCount);
                Assert.Equal(original.VirtualChannelFrameCount, f.VirtualChannelFrameCount);
                Assert.Equal(original.FirstHeaderPointerValue, f.FirstHeaderPointerValue);
                Assert.Equal(original.OperationalControlField, f.OperationalControlField);
                Assert.True(original.DataField.Span.SequenceEqual(f.DataField.Span));
            }
        }
    }

    private static byte[] WithFixedCrc(byte[] frame)
    {
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(frame.Length - 2), Crc16Ccitt.Compute(frame.AsSpan(0, frame.Length - 2)));
        return frame;
    }

    [Fact]
    [Trait("Requirement", "REQ-FRM-03")]
    public void Structurally_invalid_frames_are_rejected_with_a_reason_even_when_crc_is_valid()
    {
        byte[] Valid() => new TransferFrame(1, 1, 0, 0, 0, new byte[8]).Encode(Small);

        Assert.Equal(FrameError.WrongLength, TransferFrame.Decode(Valid().AsSpan(0, 15), Small).Error);
        Assert.Equal(FrameError.WrongLength, TransferFrame.Decode([.. Valid(), 0], Small).Error);

        byte[] version = Valid();
        version[0] |= 0x40;
        Assert.Equal(FrameError.UnsupportedVersion, TransferFrame.Decode(WithFixedCrc(version), Small).Error);

        byte[] sync = Valid();
        sync[4] |= 0x40;
        Assert.Equal(FrameError.InvalidDataFieldStatus, TransferFrame.Decode(WithFixedCrc(sync), Small).Error);

        byte[] pointer = Valid();
        pointer[5] = 8;                                     // 데이터 필드(0..7) 밖
        Assert.Equal(FrameError.InvalidDataFieldStatus, TransferFrame.Decode(WithFixedCrc(pointer), Small).Error);

        byte[] ocf = Valid();
        ocf[1] |= 0x01;                                     // 형식에 없는 OCF 플래그
        Assert.Equal(FrameError.InvalidDataFieldStatus, TransferFrame.Decode(WithFixedCrc(ocf), Small).Error);

        byte[] crc = Valid();
        crc[^1] ^= 1;
        Assert.Equal(FrameError.CrcMismatch, TransferFrame.Decode(crc, Small).Error);
    }

    [Fact]
    [Trait("Requirement", "REQ-FRM-03")]
    public void Invalid_construction_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameConfig(14));        // 데이터 6 바이트 < 7
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameConfig(2055));      // 데이터 2047 바이트 > 2046
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferFrame(1024, 0, 0, 0, 0, new byte[8]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferFrame(0, 8, 0, 0, 0, new byte[8]));
        Assert.Throws<InvalidOperationException>(() => new TransferFrame(0, 0, 0, 0, 0, new byte[7]).Encode(Small));
        Assert.Throws<InvalidOperationException>(() => new TransferFrame(0, 0, 0, 0, 8, new byte[8]).Encode(Small));
    }

    [Fact]
    [Trait("Requirement", "REQ-FRM-01")]
    public void Frames_without_frame_error_control_round_trip()
    {
        // FECF 를 끄는 형식도 지원한다. 이 경로는 CRC 검사를 건너뛰므로 따로 확인한다.
        var config = new FrameConfig(128, hasFrameErrorControl: false);
        Assert.Equal(122, config.DataFieldLength);
        var data = new byte[config.DataFieldLength];
        new Random(5).NextBytes(data);

        byte[] encoded = new TransferFrame(0x155, 2, 7, 9, 0, data).Encode(config);
        Assert.Equal(config.FrameLength, encoded.Length);
        FrameDecodeResult decoded = TransferFrame.Decode(encoded, config);
        Assert.True(decoded.IsValid);
        Assert.Equal(data, decoded.Frame!.DataField.ToArray());

        // FECF 가 없으면 마지막 바이트도 데이터다 — 바꿔도 프레임은 유효하고, 오류는 걸러지지 않는다.
        encoded[^1] ^= 0xFF;
        Assert.True(TransferFrame.Decode(encoded, config).IsValid);
    }

    [Fact]
    [Trait("Requirement", "REQ-FRM-03")]
    public void Data_field_length_limits_are_inclusive_at_both_ends()
    {
        // 거부 쪽(14 · 2055)만 확인하면 경계 비교를 `<` → `<=` 로 바꾼 결함이 드러나지 않는다.
        Assert.Equal(SpacePacket.PrimaryHeaderLength + 1, new FrameConfig(15).DataFieldLength);
        Assert.Equal(FrameConfig.MaxDataFieldLength, new FrameConfig(2054).DataFieldLength);
    }
}

public class SpacePacketTests
{
    [Fact]
    [Trait("Requirement", "REQ-PKT-01")]
    public void Encoding_follows_the_ccsds_bit_layout()
    {
        byte[] bytes = new SpacePacket(0x123, 0x1ABC, [0xAA, 0xBB], SequenceFlags.Unsegmented,
            PacketType.Telecommand, hasSecondaryHeader: true).Encode();
        // 버전 000 | 유형 1 | 부헤더 1 | APID 00100100011 → 0x1923 · 순서 11 | 01101010111100 → 0xDABC · 길이-1 → 0x0001
        Assert.Equal(new byte[] { 0x19, 0x23, 0xDA, 0xBC, 0x00, 0x01, 0xAA, 0xBB }, bytes);
    }

    [Fact]
    [Trait("Requirement", "REQ-PKT-01")]
    public void Random_packets_round_trip()
    {
        var rnd = new Random(11);
        for (int i = 0; i < 5_000; i++)
        {
            var data = new byte[rnd.Next(1, 2_000)];
            rnd.NextBytes(data);
            var p = new SpacePacket((ushort)rnd.Next(2048), (ushort)rnd.Next(16384), data,
                (SequenceFlags)rnd.Next(4), (PacketType)rnd.Next(2), rnd.Next(2) == 1);
            Assert.Equal(p, SpacePacket.Decode(p.Encode()));
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-PKT-02")]
    public void Field_boundaries_are_enforced()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpacePacket(2048, 0, [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpacePacket(0, 16384, [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpacePacket(0, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpacePacket(0, 0, new byte[65537]));
        Assert.Equal(65536, SpacePacket.Decode(new SpacePacket(2047, 16383, new byte[65536]).Encode()).Data.Length);
    }

    [Fact]
    [Trait("Requirement", "REQ-PKT-02")]
    public void Malformed_packets_are_rejected()
    {
        byte[] ok = new SpacePacket(5, 1, [1, 2, 3]).Encode();
        Assert.Throws<FormatException>(() => SpacePacket.Decode(ok.AsSpan(0, 5)));
        Assert.Throws<FormatException>(() => SpacePacket.Decode(ok.AsSpan(0, 8)));
        Assert.Throws<FormatException>(() => SpacePacket.Decode([.. ok, 0]));
        byte[] version = (byte[])ok.Clone();
        version[0] |= 0x20;
        Assert.Throws<FormatException>(() => SpacePacket.Decode(version));
    }

    [Fact]
    [Trait("Requirement", "REQ-EXT-06")]
    public void Error_control_check_is_false_when_the_packet_cannot_hold_a_crc()
    {
        // PEC 는 데이터 필드 끝 2 바이트다. 데이터가 3 바이트 미만이면 검사할 것이 없다.
        Assert.False(new SpacePacket(1, 0, [1, 2]).HasValidErrorControl());
        Assert.True(SpacePacket.WithErrorControl(1, 0, [1, 2]).HasValidErrorControl());
    }

    [Fact]
    [Trait("Requirement", "REQ-PKT-01")]
    public void Value_equality_distinguishes_packets_by_content()
    {
        var a = new SpacePacket(5, 1, [1, 2, 3]);
        var b = new SpacePacket(5, 1, [1, 2, 3]);
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a.Equals((object?)null));
        Assert.NotEqual(a, new SpacePacket(5, 1, [1, 2, 4]));
        Assert.Contains("apid=5", a.ToString(), StringComparison.Ordinal);
    }
}
