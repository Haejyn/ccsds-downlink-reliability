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
    [InlineData(4)]
    [InlineData(5)]
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

    [Fact]
    [Trait("Requirement", "REQ-RS-04")]
    public void Interleaving_spreads_a_burst_so_it_stays_correctable()
    {
        // 인터리빙 깊이 I 면 연속 I·16 심볼 버스트가 부호어마다 16 개씩 흩어진다 — 딱 정정 능력이다.
        foreach (int depth in new[] { 4, 5 })
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
}
