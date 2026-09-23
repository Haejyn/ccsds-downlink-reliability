using SpaceLink.ChannelCoding;

namespace SpaceLink.Tests;

/// <summary>
/// 처리량을 위해 바꾼 경로(§8.6)가 **예전과 같은 답**을 내는지. 신드롬은 CPU 에 따라 벡터 판(SSSE3)과 표 조회 판 중 하나만 돌므로,
/// 두 판을 직접 불러 서로, 그리고 시험 안에서 따로 쓴 Horner 계산과 대조한다 — 어느 기계에서 돌아도 두 판이 모두 시험된다.
/// </summary>
public class FastPathTests
{
    [Fact]
    [Trait("Requirement", "REQ-RS-02")]
    public void Vector_and_table_syndromes_match_an_independent_horner_evaluation()
    {
        var rnd = new Random(8160);
        var codeword = new byte[ReedSolomonCodec.SymbolsPerCodeword];
        Span<byte> vector = stackalloc byte[ReedSolomonCodec.ParitySymbolsPerCodeword];
        Span<byte> table = stackalloc byte[ReedSolomonCodec.ParitySymbolsPerCodeword];
        for (int trial = 0; trial < 500; trial++)
        {
            rnd.NextBytes(codeword);
            int fill = trial % 3 == 0 ? 0 : rnd.Next(0, 223);
            codeword.AsSpan(0, fill).Clear();   // 가상 채움 자리는 0 이다

            bool tableClean = Syndromes.ComputeScalar(codeword, fill, table);
            bool vectorClean = tableClean;
            table.CopyTo(vector);
            if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported)   // ARM 러너라면 벡터 판은 없다 — 표 조회 판만 대조한다
            {
                vectorClean = Syndromes.ComputeVector(codeword, fill, vector);
            }

            for (int n = 0; n < ReedSolomonCodec.ParitySymbolsPerCodeword; n++)
            {
                byte expected = Horner(codeword, ReedSolomonCodec.RootAt(n));
                Assert.True(expected == table[n], $"trial {trial} fill {fill}: 표 조회 판 S{n} = {table[n]:X2}, Horner {expected:X2}");
                Assert.True(expected == vector[n], $"trial {trial} fill {fill}: 벡터 판 S{n} = {vector[n]:X2}, Horner {expected:X2}");
            }

            Assert.Equal(vectorClean, tableClean);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-02")]
    public void A_valid_codeword_has_all_zero_syndromes_in_both_paths()
    {
        var codec = new ReedSolomonCodec();
        var data = new byte[codec.DataLength];
        new Random(32).NextBytes(data);
        byte[] codeword = codec.Encode(data);
        for (int i = 0; i < codeword.Length; i++)
        {
            codeword[i] = DualBasisTransform.ToConventional(codeword[i]);   // 신드롬은 관례 기저에서 계산한다
        }

        Span<byte> syndromes = stackalloc byte[ReedSolomonCodec.ParitySymbolsPerCodeword];
        Assert.True(Syndromes.ComputeScalar(codeword, 0, syndromes));
        if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported)
        {
            Assert.True(Syndromes.ComputeVector(codeword, 0, syndromes));
        }

        codeword[100] ^= 0x5A;
        Assert.False(Syndromes.ComputeScalar(codeword, 0, syndromes));
        if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported)
        {
            Assert.False(Syndromes.ComputeVector(codeword, 0, syndromes));
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-EXT-08")]
    public void One_codec_decodes_concurrently_with_the_same_results_as_sequentially()
    {
        // 병렬 복호(§8.6)는 코덱 인스턴스 하나를 여러 스레드가 같이 쓴다 — 생성 뒤 상태가 없다는 전제를 결과로 고정한다.
        var codec = new ChannelCodec(128, sequence: PseudorandomSequence.Standard131071);
        var rnd = new Random(1530);
        byte[][] codeblocks = Enumerable.Range(0, 2_000).Select(k =>
        {
            var frame = new byte[128];
            rnd.NextBytes(frame);
            byte[] codeblock = codec.EncodeCadu(frame)[FrameSynchronizer.MarkerLength..];
            for (int e = 0; e < k % 17; e++)   // 오류 0 … 16 개 — 정정 경로도 병렬로 돈다
            {
                codeblock[rnd.Next(codeblock.Length)] ^= (byte)rnd.Next(1, 256);
            }

            return codeblock;
        }).ToArray();

        ChannelDecodeResult[] sequential = codeblocks.Select(b => codec.DecodeCodeblock(b)).ToArray();
        var parallel = new ChannelDecodeResult[codeblocks.Length];
        Parallel.For(0, codeblocks.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => parallel[i] = codec.DecodeCodeblock(codeblocks[i]));

        for (int i = 0; i < codeblocks.Length; i++)
        {
            Assert.Equal(sequential[i].Succeeded, parallel[i].Succeeded);
            Assert.Equal(sequential[i].CorrectedSymbols, parallel[i].CorrectedSymbols);
            Assert.Equal(sequential[i].TransferFrame, parallel[i].TransferFrame);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-RS-02")]
    public void Errors_whose_first_syndrome_is_zero_are_still_corrected()
    {
        // Berlekamp-Massey 는 첫 불일치(discrepancy)가 0 이면 그 단계를 건너뛴다. 무작위 오류로 첫 신드롬이 0 이 될 확률은 1/256 이라
        // 기존 시험은 한 번도 그 길을 타지 않았고, 건너뛰는 `continue` 를 지운 뮤턴트가 살아남았다 — 지우면 0 으로 나누게 된다.
        // 오류 두 개를 무작위로 넣어 보며 S0 = 0 인 모양을 찾아(시험 안의 독립 Horner 로 판정) 그 모양이 정정되는지 본다.
        var codec = new ReedSolomonCodec();
        var rnd = new Random(1120);
        var data = new byte[codec.DataLength];
        rnd.NextBytes(data);
        byte[] clean = codec.Encode(data);
        var conventional = new byte[clean.Length];

        for (int attempt = 0; attempt < 20_000; attempt++)
        {
            byte[] received = (byte[])clean.Clone();
            int first = rnd.Next(received.Length);
            int second = (first + 1 + rnd.Next(received.Length - 1)) % received.Length;
            received[first] ^= (byte)rnd.Next(1, 256);
            received[second] ^= (byte)rnd.Next(1, 256);
            for (int i = 0; i < received.Length; i++)
            {
                conventional[i] = DualBasisTransform.ToConventional(received[i]);
            }

            if (Horner(conventional, ReedSolomonCodec.RootAt(0)) != 0)
            {
                continue;
            }

            var decoded = new byte[codec.DataLength];
            ReedSolomonResult result = codec.Decode(received, decoded);
            Assert.True(result.Succeeded, $"attempt {attempt}: S0 = 0 인 오류 두 개를 정정하지 못했다");
            Assert.Equal(2, result.CorrectedSymbols);
            Assert.Equal(data, decoded);
            return;
        }

        Assert.Fail("S0 = 0 인 오류 모양을 찾지 못했다 — 시도 수를 늘려야 한다");
    }

    /// <summary>Σ c_i·x^(254−i) — 구현의 표와 무관하게 GaloisField256 곱셈만으로.</summary>
    private static byte Horner(byte[] codeword, byte x)
    {
        byte value = 0;
        foreach (byte symbol in codeword)
        {
            value = (byte)(GaloisField256.Multiply(value, x) ^ symbol);
        }

        return value;
    }
}
