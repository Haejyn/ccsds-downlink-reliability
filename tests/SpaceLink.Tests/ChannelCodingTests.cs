using System.Globalization;
using SpaceLink.ChannelCoding;

namespace SpaceLink.Tests;

public class GaloisFieldTests
{
    [Fact]
    [Trait("Requirement", "REQ-RS-01")]
    public void Field_generator_is_primitive_so_powers_cover_every_non_zero_element()
    {
        // 원시가 아니면 α 의 거듭제곱이 원소를 다 훑지 못하고, 그러면 RS 복호가 조용히 틀린다.
        var seen = new HashSet<byte>();
        for (int power = 0; power < GaloisField256.NonZeroElements; power++)
        {
            Assert.True(seen.Add(GaloisField256.Exp(power)), $"α^{power} repeated");
        }

        Assert.Equal(GaloisField256.NonZeroElements, seen.Count);
        Assert.DoesNotContain((byte)0, seen);
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-01")]
    public void Multiplication_and_division_are_inverse_for_every_pair()
    {
        for (int a = 0; a < 256; a++)
        {
            for (int b = 1; b < 256; b++)
            {
                byte product = GaloisField256.Multiply((byte)a, (byte)b);
                Assert.Equal((byte)a, GaloisField256.Divide(product, (byte)b));
            }
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-01")]
    public void Inverse_multiplied_back_is_one_and_logarithm_round_trips()
    {
        for (int value = 1; value < 256; value++)
        {
            Assert.Equal((byte)1, GaloisField256.Multiply((byte)value, GaloisField256.Inverse((byte)value)));
            Assert.Equal((byte)value, GaloisField256.Exp(GaloisField256.Log((byte)value)));
        }

        Assert.Equal((byte)0, GaloisField256.Multiply(0, 7));
        Assert.Equal((byte)0, GaloisField256.Divide(0, 7));
        Assert.Equal((byte)1, GaloisField256.Exp(0));
        Assert.Equal(GaloisField256.Exp(1), GaloisField256.Exp(GaloisField256.NonZeroElements + 1));
        Assert.Equal(GaloisField256.Exp(GaloisField256.NonZeroElements - 1), GaloisField256.Exp(-1));
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-01")]
    public void Zero_has_no_logarithm_and_no_inverse()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GaloisField256.Log(0));
        Assert.Throws<DivideByZeroException>(() => GaloisField256.Inverse(0));
        Assert.Throws<DivideByZeroException>(() => GaloisField256.Divide(5, 0));
    }
}

public class ReedSolomonTests
{
    public static IEnumerable<object[]> CorrectableErrorCounts() =>
        Enumerable.Range(0, ReedSolomonCodec.CorrectableSymbols + 1).Select(t => new object[] { t });

    private static byte[] RandomData(Random rnd, int length)
    {
        var data = new byte[length];
        rnd.NextBytes(data);
        return data;
    }

    /// <summary>서로 다른 위치 count 개에 0 이 아닌 값을 XOR 한다 — 반드시 심볼 오류 count 개가 된다.</summary>
    private static void InjectSymbolErrors(Random rnd, Span<byte> codeblock, int count)
    {
        var positions = new HashSet<int>();
        while (positions.Count < count)
        {
            positions.Add(rnd.Next(codeblock.Length));
        }

        foreach (int position in positions)
        {
            byte delta = (byte)rnd.Next(1, 256);
            codeblock[position] ^= delta;
        }
    }

    [Theory]
    [Trait("Requirement", "REQ-RS-01")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    public void Encoding_then_decoding_returns_the_original_data(int interleavingDepth)
    {
        var codec = new ReedSolomonCodec(interleavingDepth);
        var rnd = new Random(1234 + interleavingDepth);
        for (int trial = 0; trial < 20; trial++)
        {
            byte[] data = RandomData(rnd, codec.DataLength);
            byte[] codeblock = codec.Encode(data);
            Assert.Equal(codec.CodeblockLength, codeblock.Length);

            var decoded = new byte[codec.DataLength];
            ReedSolomonResult result = codec.Decode(codeblock, decoded);
            Assert.True(result.Succeeded);
            Assert.Equal(0, result.CorrectedSymbols);
            Assert.Equal(data, decoded);
        }
    }

    [Theory]
    [Trait("Requirement", "REQ-RS-02")]
    [MemberData(nameof(CorrectableErrorCounts))]
    public void Up_to_sixteen_symbol_errors_per_codeword_are_corrected(int errorCount)
    {
        // 정정 능력 경계를 말이 아니라 실행으로 고정한다 — t = 0 … 16 은 전부 원본으로 돌아와야 한다.
        var codec = new ReedSolomonCodec();
        foreach (int seed in new[] { 7, 8, 9, 10, 11 })
        {
            var rnd = new Random((seed * 100) + errorCount);
            byte[] data = RandomData(rnd, codec.DataLength);
            byte[] codeblock = codec.Encode(data);
            InjectSymbolErrors(rnd, codeblock, errorCount);

            var decoded = new byte[codec.DataLength];
            ReedSolomonResult result = codec.Decode(codeblock, decoded);
            Assert.True(result.Succeeded, $"t={errorCount} seed={seed} declared failure");
            Assert.Equal(errorCount, result.CorrectedSymbols);
            Assert.Equal(data, decoded);
        }
    }

    [Theory]
    [Trait("Requirement", "REQ-RS-07")]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(16)]
    public void Interleaving_depths_the_standard_does_not_allow_are_refused(int depth)
    {
        // §4.3.5.1 — I = 1, 2, 3, 4, 5, 8 만. 6·7 은 사이에 낀 값, 9·16 은 8 을 넘는 값이다.
        // 프레임 길이는 깊이의 배수로 골라, 거부 이유가 가상 채움 규칙이 아니라 깊이 자체임을 분명히 한다.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new ReedSolomonCodec(depth));
        Assert.Equal(depth, ex.ActualValue);
        ex = Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelCodec(Math.Max(1, depth) * 100, depth));
        Assert.Equal(depth, ex.ActualValue);
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-07")]
    public void Every_interleaving_depth_the_standard_allows_is_accepted()
    {
        Assert.Equal([1, 2, 3, 4, 5, 8], ReedSolomonCodec.AllowedInterleavingDepths);
        foreach (int depth in ReedSolomonCodec.AllowedInterleavingDepths)
        {
            Assert.Equal(255 * depth, new ReedSolomonCodec(depth).CodeblockLength);
            Assert.Equal(depth * 100, new ChannelCodec(depth * 100, depth).TransferFrameLength);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-04")]
    public void Interleaving_spreads_a_burst_so_it_stays_correctable()
    {
        // 인터리빙 깊이 I 면 연속 I·16 심볼 버스트가 부호어마다 16 개씩 흩어진다 — 딱 정정 능력이다.
        foreach (int depth in new[] { 2, 4, 5, 8 })
        {
            var codec = new ReedSolomonCodec(depth);
            var rnd = new Random(4242 + depth);
            byte[] data = RandomData(rnd, codec.DataLength);
            byte[] codeblock = codec.Encode(data);

            int burst = depth * ReedSolomonCodec.CorrectableSymbols;
            int start = 300;
            for (int i = 0; i < burst; i++)
            {
                codeblock[start + i] ^= (byte)rnd.Next(1, 256);
            }

            var decoded = new byte[codec.DataLength];
            ReedSolomonResult result = codec.Decode(codeblock, decoded);
            Assert.True(result.Succeeded, $"depth={depth} burst={burst} declared failure");
            Assert.Equal(data, decoded);

            // 같은 버스트를 인터리빙 없이 맞으면 한 부호어에 몰려 정정 능력을 넘는다.
            // (깊이 1 의 코드블록은 255 바이트뿐이라 버스트가 들어갈 자리에서 시작한다.)
            var flat = new ReedSolomonCodec(1);
            byte[] flatData = RandomData(new Random(99), flat.DataLength);
            byte[] flatBlock = flat.Encode(flatData);
            int flatStart = 80;
            for (int i = 0; i < burst; i++)
            {
                flatBlock[flatStart + i] ^= 0xFF;
            }

            var flatDecoded = new byte[flat.DataLength];
            ReedSolomonResult flatResult = flat.Decode(flatBlock, flatDecoded);
            Assert.False(flatResult.Succeeded && flatDecoded.SequenceEqual(flatData),
                "a burst larger than t must not come back as the original without interleaving");
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-03")]
    public void Beyond_correction_capability_the_decoder_fails_or_miscorrects_and_crc_catches_it()
    {
        // 핵심 계약과 이어지는 시험: 정정 능력을 넘으면 복호기가 "성공" 이라 말하면서 다른 부호어로
        // 잘못 정정할 수 있다(오정정). 그때 프레임 CRC 가 걸러 주는지를 실제로 센다.
        const int frameLength = 128;
        var codec = new ReedSolomonCodec();
        var report = new List<string>();
        int silentCorruptionPassingCrc = 0;

        foreach (int errorCount in new[] { 17, 18, 20, 24, 32 })
        {
            int detected = 0, miscorrected = 0, frameTouched = 0;
            const int trials = 200;
            foreach (int seed in new[] { 21, 22, 23 })
            {
                var rnd = new Random((seed * 1000) + errorCount);
                for (int trial = 0; trial < trials; trial++)
                {
                    byte[] frame = RandomData(rnd, frameLength);
                    ushort crc = Crc16Ccitt.Compute(frame.AsSpan(0, frameLength - 2));
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(frameLength - 2), crc);

                    var data = new byte[codec.DataLength];
                    frame.CopyTo(data, 0);
                    byte[] codeblock = codec.Encode(data);
                    InjectSymbolErrors(rnd, codeblock, errorCount);

                    var decoded = new byte[codec.DataLength];
                    ReedSolomonResult result = codec.Decode(codeblock, decoded);
                    if (!result.Succeeded)
                    {
                        detected++;
                        continue;
                    }

                    Assert.False(decoded.SequenceEqual(data), "more errors than t must not decode back to the original");
                    miscorrected++;

                    ReadOnlySpan<byte> decodedFrame = decoded.AsSpan(0, frameLength);
                    if (decodedFrame.SequenceEqual(frame))
                    {
                        continue;   // 오정정이 패딩만 건드렸다 — 프레임은 무사하다
                    }

                    frameTouched++;
                    ushort recomputed = Crc16Ccitt.Compute(decodedFrame[..(frameLength - 2)]);
                    ushort carried = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(decodedFrame[(frameLength - 2)..]);
                    if (recomputed == carried)
                    {
                        silentCorruptionPassingCrc++;
                    }
                }
            }

            int total = trials * 3;
            report.Add(string.Create(CultureInfo.InvariantCulture,
                $"t={errorCount,2}: 실패선언 {detected,4}/{total}  오정정 {miscorrected,4}  그중 프레임 훼손 {frameTouched,4}  CRC 통과 {silentCorruptionPassingCrc,3}"));
        }

        foreach (string line in report)
        {
            Console.WriteLine(line);
        }

        // 계약: 오정정으로 프레임이 훼손됐는데 CRC 까지 맞아 떨어지는 경우는 이 시드들에서 0 이어야 한다.
        // (이론상 2^-16 확률로 가능하다 — 0 을 보장이 아니라 실측으로 적는다.)
        Assert.Equal(0, silentCorruptionPassingCrc);
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-03")]
    public void Arbitrary_garbage_never_throws_and_is_reported_as_failure()
    {
        // 정정 실패 경로에서 예외로 죽지 않는다 — 수신기의 REQ-EXT-07 과 같은 계약이다.
        // (복호기의 도달 불가 방어 가지를 지웠으므로, 그 판단이 틀렸다면 여기서 예외로 드러난다.)
        var codec = new ReedSolomonCodec();
        var rnd = new Random(31337);
        var data = new byte[codec.DataLength];
        var block = new byte[codec.CodeblockLength];
        int succeeded = 0;
        for (int trial = 0; trial < 3_000; trial++)
        {
            rnd.NextBytes(block);
            ReedSolomonResult result = codec.Decode(block, data);
            if (result.Succeeded)
            {
                succeeded++;
            }
        }

        // 무작위 바이트가 우연히 부호어 근처일 확률은 지극히 낮다 — 대부분 실패로 보고돼야 한다.
        Assert.True(succeeded < 100, $"무작위 입력 3,000 건 중 {succeeded} 건이 성공으로 보고됐다");
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-01")]
    public void Wrong_sized_buffers_are_refused()
    {
        var codec = new ReedSolomonCodec();
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReedSolomonCodec(0));
        Assert.Throws<ArgumentException>(() => codec.Encode(new byte[10]));
        Assert.Throws<ArgumentException>(() => codec.Decode(new byte[10], new byte[codec.DataLength]));
        Assert.Throws<ArgumentException>(() => codec.Decode(new byte[codec.CodeblockLength], new byte[10]));
    }
}

public class PseudorandomizerTests
{
    [Fact]
    [Trait("Requirement", "REQ-PN-01")]
    public void Applying_the_randomizer_twice_returns_the_original()
    {
        var rnd = new Random(5150);
        for (int trial = 0; trial < 50; trial++)
        {
            var original = new byte[rnd.Next(1, 600)];
            rnd.NextBytes(original);
            var working = (byte[])original.Clone();

            Pseudorandomizer.Apply(working);
            Assert.NotEqual(original, working);     // 실제로 뒤섞여야 한다
            Pseudorandomizer.Apply(working);
            Assert.Equal(original, working);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-PN-01")]
    public void Sequence_starts_with_all_ones_and_repeats_every_255_bytes()
    {
        byte[] sequence = Pseudorandomizer.Sequence(Pseudorandomizer.SequencePeriod * 2);
        Assert.Equal((byte)0xFF, sequence[0]);
        Assert.True(sequence.AsSpan(0, Pseudorandomizer.SequencePeriod)
            .SequenceEqual(sequence.AsSpan(Pseudorandomizer.SequencePeriod)));

        // 주기 안에서는 되풀이되지 않아야 한다 (최대 길이 LFSR).
        Assert.False(sequence.AsSpan(0, 128).SequenceEqual(sequence.AsSpan(128, 128)));

        // 255 의 약수 주기로도 되풀이되면 안 된다 — 탭을 잘못 옮기면 주기가 조용히 짧아진다.
        // (실제로 그런 적이 있다: 지수를 비트 번호로 옮겼더니 주기가 217 이었다.)
        foreach (int divisor in new[] { 1, 3, 5, 15, 17, 51, 85 })
        {
            Assert.False(
                sequence.AsSpan(0, 200).SequenceEqual(sequence.AsSpan(divisor, 200)),
                $"주기가 {divisor} 바이트로 짧아졌다");
        }
        Assert.Empty(Pseudorandomizer.Sequence(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pseudorandomizer.Sequence(-1));
    }

    /// <summary>
    /// CCSDS 131.0-B-5 §10.4.3 NOTE 2 (p.10-3) 가 255 비트 랜덤화기의 처음 40 비트로 싣고 있는 값.
    /// <c>1111 1111 0100 1000 0000 1110 1100 0000 1001 1010</c> — 맨 왼쪽이 코드블록의 첫 비트와 XOR 되는 첫 비트다.
    /// </summary>
    private static readonly byte[] StandardFirstFortyBits = [0xFF, 0x48, 0x0E, 0xC0, 0x9A];

    [Fact]
    [Trait("Requirement", "REQ-PN-01")]
    public void Sequence_starts_with_the_forty_bits_the_standard_prints()
    {
        // 구현이 아니라 **표준 문서**가 기대값의 출처다. 자기 역원·주기 시험은 다항식을 잘못 옮겨도(주기 217 사건)
        // 통과할 수 있었고, 이 다섯 바이트가 그 틈을 막는다.
        Assert.True(StandardFirstFortyBits.AsSpan().SequenceEqual(Pseudorandomizer.Sequence(5)),
            $"수열의 처음 40 비트가 표준의 FF 48 0E C0 9A 와 다르다 (실제 {Convert.ToHexString(Pseudorandomizer.Sequence(5))})");
    }

    [Fact]
    [Trait("Requirement", "REQ-PN-01")]
    public void Sequence_matches_an_independent_linear_recurrence_over_three_periods()
    {
        // 기준 구현이 표준과 같은 수열을 내는지 먼저 못 박는다 — 기준이 틀렸으면 아래 대조가 아무것도 증명하지 못한다.
        Assert.True(StandardFirstFortyBits.AsSpan().SequenceEqual(Downlink.ReferencePnSequence(5)),
            "시험 쪽 기준 수열이 표준의 처음 40 비트와 다르다");

        // 세 주기(765 바이트) — 첫 주기의 탭 실수와 주기 경계에서 되감기는 실수를 모두 드러낸다.
        int length = Pseudorandomizer.SequencePeriod * 3;
        byte[] expected = Downlink.ReferencePnSequence(length);
        byte[] actual = Pseudorandomizer.Sequence(length);
        for (int i = 0; i < length; i++)
        {
            Assert.True(expected[i] == actual[i],
                $"{i} 번째 바이트가 독립 구현과 다르다 (기대 {expected[i]:X2}, 실제 {actual[i]:X2})");
        }
    }

    /// <summary>
    /// CCSDS 131.0-B-5 §10.4.3 NOTE 2 가 131071 비트 랜덤화기의 처음 40 비트로 싣는 값.
    /// <c>0001 1100 0111 0001 1011 1001 0001 1011 1010 1001</c>.
    /// </summary>
    private static readonly byte[] Standard131071FirstFortyBits = [0x1C, 0x71, 0xB9, 0x1B, 0xA9];

    [Fact]
    [Trait("Requirement", "REQ-PN-02")]
    public void Standard_sequence_starts_with_the_forty_bits_the_standard_prints()
    {
        // 초기값 문자열을 왼쪽부터 첫 비트로 읽으면 C7 1C 6E 46 EA 가 나온다 — 방향을 틀리면 여기서 드러난다.
        byte[] actual = Pseudorandomizer.Sequence(5, PseudorandomSequence.Standard131071);
        Assert.True(Standard131071FirstFortyBits.AsSpan().SequenceEqual(actual),
            $"131071 비트 수열의 처음 40 비트가 표준의 1C 71 B9 1B A9 와 다르다 (실제 {Convert.ToHexString(actual)})");
    }

    [Fact]
    [Trait("Requirement", "REQ-PN-02")]
    public void Standard_sequence_matches_an_independent_recurrence_over_the_whole_usable_length()
    {
        // 기준 구현이 표준과 같은지 먼저 못 박는다.
        Assert.True(Standard131071FirstFortyBits.AsSpan().SequenceEqual(Downlink.ReferencePn131071Sequence(5)),
            "시험 쪽 기준 수열이 표준의 처음 40 비트와 다르다");

        // 쓸 수 있는 길이 전부(16,383 바이트) — 표준의 최대 코드블록(I = 8, 2,040 바이트)을 넉넉히 덮는다.
        int length = Pseudorandomizer.Standard131071MaxBytes;
        byte[] expected = Downlink.ReferencePn131071Sequence(length);
        byte[] actual = Pseudorandomizer.Sequence(length, PseudorandomSequence.Standard131071);
        int firstDifference = expected.AsSpan().CommonPrefixLength(actual);
        Assert.True(firstDifference == length,
            $"{firstDifference} 번째 바이트부터 독립 구현과 다르다");
    }

    [Fact]
    [Trait("Requirement", "REQ-PN-02")]
    public void Standard_sequence_is_self_inverse_has_the_full_period_and_is_not_the_legacy_one()
    {
        var rnd = new Random(131071);
        var original = new byte[2040];                    // 표준의 최대 코드블록 (I = 8)
        rnd.NextBytes(original);
        byte[] working = (byte[])original.Clone();
        Pseudorandomizer.Apply(working, PseudorandomSequence.Standard131071);
        Assert.NotEqual(original, working);
        Pseudorandomizer.Apply(working, PseudorandomSequence.Standard131071);
        Assert.Equal(original, working);

        // 255 비트 수열처럼 255 바이트마다 되풀이되면 안 된다.
        byte[] sequence = Pseudorandomizer.Sequence(1024, PseudorandomSequence.Standard131071);
        Assert.False(sequence.AsSpan(0, 255).SequenceEqual(sequence.AsSpan(255, 255)), "255 바이트 주기가 보인다 — 옛 수열이다");
        Assert.False(sequence.AsSpan(0, 64).SequenceEqual(Pseudorandomizer.Sequence(64, PseudorandomSequence.Legacy255)));

        Assert.Equal(131071, Pseudorandomizer.Standard131071PeriodBits);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Pseudorandomizer.Apply(new byte[Pseudorandomizer.Standard131071MaxBytes + 1], PseudorandomSequence.Standard131071));
    }

    [Theory]
    [Trait("Requirement", "REQ-PN-02")]
    [InlineData(PseudorandomSequence.Standard131071)]
    [InlineData(PseudorandomSequence.Legacy255)]
    public void Channel_codec_randomizes_the_codeblock_with_the_chosen_sequence(PseudorandomSequence sequence)
    {
        // CADU = ASM + (RS 코드블록 ⊕ PN 수열). RS 부호기를 따로 불러 코드블록을 만들고 수열을 걷어 내면 같아야 한다.
        // 기본값이 표준(131071)인지도 같이 본다 — 수열 종류는 물리 채널마다 미리 정하는 값이다(§10.1).
        const int frameLength = 128;
        var frame = new byte[frameLength];
        new Random(10431).NextBytes(frame);

        var codec = sequence == PseudorandomSequence.Standard131071
            ? new ChannelCodec(frameLength)                          // 기본값
            : new ChannelCodec(frameLength, sequence: sequence);
        byte[] cadu = codec.EncodeCadu(frame);

        var reedSolomon = new ReedSolomonCodec(1, ReedSolomonCodec.DataSymbolsPerCodeword - frameLength);
        byte[] codeblock = reedSolomon.Encode(frame);
        byte[] pn = Pseudorandomizer.Sequence(codeblock.Length, sequence);
        for (int i = 0; i < codeblock.Length; i++)
        {
            codeblock[i] ^= pn[i];
        }

        Assert.True(codeblock.AsSpan().SequenceEqual(cadu.AsSpan(FrameSynchronizer.MarkerLength)),
            $"CADU 의 코드블록이 RS 코드블록 ⊕ {sequence} 수열과 다르다");
        Assert.Equal(frame, codec.DecodeCodeblock(cadu.AsSpan(FrameSynchronizer.MarkerLength)).TransferFrame);
    }
}
