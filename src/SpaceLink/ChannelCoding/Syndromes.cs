using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SpaceLink.ChannelCoding;

/// <summary>
/// RS 신드롬 32 개 — 수신 체인 시간의 약 90 % 가 여기였다(단계별 실측, 시험 보고서 §8.6).
///
/// 신드롬 n 은 S_n = Σ_i r_i · w(i, n), w(i, n) = β^((112+n)·(254−i)) 이다(r_i 는 x^(254−i) 의 계수).
/// Horner 로 신드롬마다 255 번 곱하던 것을 **심볼마다 32 개를 한꺼번에** 더하는 순서로 바꿨다 — 심볼 r_i 하나에 대해
/// 32 개의 곱 r_i · w(i, n) 은 **같은 상수 r_i 를 곱하는 것**이라, GF(2^8) 곱이 XOR 에 대해 선형이라는 성질로
/// 가중치를 아래·위 4 비트로 나눠 16 칸짜리 표 두 개(r_i·k, r_i·(k≪4))를 PSHUFB 한 번씩으로 찾을 수 있다.
/// 16 바이트 레지스터 둘이 신드롬 32 개다.
/// </summary>
internal static class Syndromes
{
    private const int Count = ReedSolomonCodec.ParitySymbolsPerCodeword;
    private const int Symbols = ReedSolomonCodec.SymbolsPerCodeword;

    /// <summary>w(i, n) 의 아래 4 비트 — [i·32 + n].</summary>
    private static readonly byte[] WeightLow = new byte[Symbols * Count];

    /// <summary>w(i, n) 의 위 4 비트(4 칸 내린 값) — [i·32 + n].</summary>
    private static readonly byte[] WeightHigh = new byte[Symbols * Count];

    /// <summary>[c·16 + k] = c · k.</summary>
    private static readonly byte[] ProductLow = new byte[256 * 16];

    /// <summary>[c·16 + k] = c · (k ≪ 4).</summary>
    private static readonly byte[] ProductHigh = new byte[256 * 16];

#pragma warning disable CA1810 // 표 네 개를 한 번에 채운다 — 필드 초기화 식으로 나누면 같은 계산을 네 번 돈다
    static Syndromes()
#pragma warning restore CA1810
    {
        for (int i = 0; i < Symbols; i++)
        {
            for (int n = 0; n < Count; n++)
            {
                byte weight = GaloisField256.Exp(ReedSolomonCodec.RootSpacing * (ReedSolomonCodec.FirstConsecutiveRoot + n) * (Symbols - 1 - i));
                WeightLow[(i * Count) + n] = (byte)(weight & 0x0F);
                WeightHigh[(i * Count) + n] = (byte)(weight >> 4);
            }
        }

        for (int c = 0; c < 256; c++)
        {
            for (int k = 0; k < 16; k++)
            {
                ProductLow[(c * 16) + k] = GaloisField256.Multiply((byte)c, (byte)k);
                ProductHigh[(c * 16) + k] = GaloisField256.Multiply((byte)c, (byte)(k << 4));
            }
        }
    }

    /// <summary>신드롬을 채우고, 전부 0 이면(오류 없음) true. 앞쪽 fill 심볼은 가상 채움(0)이라 건너뛴다.</summary>
    public static bool Compute(ReadOnlySpan<byte> codeword, int fill, Span<byte> syndromes) =>
        Ssse3.IsSupported ? ComputeVector(codeword, fill, syndromes) : ComputeScalar(codeword, fill, syndromes);

    /// <summary>표 조회만 쓰는 판 — SSSE3 가 없는 CPU(ARM 등)와, 벡터 판을 대조하는 시험의 기준.</summary>
    internal static bool ComputeScalar(ReadOnlySpan<byte> codeword, int fill, Span<byte> syndromes)
    {
        syndromes[..Count].Clear();
        for (int i = fill; i < Symbols; i++)
        {
            int product = codeword[i] * 16;
            int row = i * Count;
            for (int n = 0; n < Count; n++)
            {
                syndromes[n] ^= (byte)(ProductLow[product + WeightLow[row + n]] ^ ProductHigh[product + WeightHigh[row + n]]);
            }
        }

        return !syndromes[..Count].ContainsAnyExcept((byte)0);
    }

    internal static bool ComputeVector(ReadOnlySpan<byte> codeword, int fill, Span<byte> syndromes)
    {
        ref byte weightLow = ref MemoryMarshal.GetArrayDataReference(WeightLow);
        ref byte weightHigh = ref MemoryMarshal.GetArrayDataReference(WeightHigh);
        ref byte productLow = ref MemoryMarshal.GetArrayDataReference(ProductLow);
        ref byte productHigh = ref MemoryMarshal.GetArrayDataReference(ProductHigh);
        Vector128<byte> first = Vector128<byte>.Zero;   // 신드롬 0 … 15
        Vector128<byte> second = Vector128<byte>.Zero;  // 신드롬 16 … 31

        for (int i = fill; i < Symbols; i++)
        {
            nuint product = (nuint)codeword[i] * 16;
            Vector128<byte> low = Vector128.LoadUnsafe(ref productLow, product);
            Vector128<byte> high = Vector128.LoadUnsafe(ref productHigh, product);
            nuint row = (nuint)(i * Count);
            first ^= Ssse3.Shuffle(low, Vector128.LoadUnsafe(ref weightLow, row))
                ^ Ssse3.Shuffle(high, Vector128.LoadUnsafe(ref weightHigh, row));
            second ^= Ssse3.Shuffle(low, Vector128.LoadUnsafe(ref weightLow, row + 16))
                ^ Ssse3.Shuffle(high, Vector128.LoadUnsafe(ref weightHigh, row + 16));
        }

        first.CopyTo(syndromes);
        second.CopyTo(syndromes[16..]);
        return (first | second) == Vector128<byte>.Zero;
    }
}
