namespace SpaceLink.ChannelCoding;

/// <summary>복호 결과 — 성공 여부와 정정한 심볼 수.</summary>
/// <remarks>
/// <see cref="Succeeded"/> 가 true 라고 해서 원본이 복원됐다는 뜻은 아니다. 정정 능력을 넘는 오류에서는
/// 복호기가 "성공" 이라 말하면서 **다른 부호어로 잘못 정정**할 수 있다 (오정정). 그 비율은 시험에서 실측한다.
/// </remarks>
public readonly record struct ReedSolomonResult(bool Succeeded, int CorrectedSymbols);

/// <summary>
/// 리드-솔로몬 RS(255,223) — 심볼 255 개 중 데이터 223, 패리티 32, 정정 능력 16 심볼.
/// 인터리빙 깊이 I 를 쓰면 코드블록은 255·I 바이트가 되고, 연속 버스트가 부호어 여러 개로 흩어진다.
///
/// ⚠ 한계: 체 생성 다항식은 CCSDS 131.0-B 의 0x187 을 쓰지만 **이중 기저(dual basis) 변환은 넣지 않았다.**
/// 정정 능력과 복호 절차는 같아도 실제 CCSDS 비트열과는 호환되지 않는다 — 시험 대상으로 쓰는 구현이다.
/// 부호 생성 다항식의 근은 α^1 … α^32 (관례 기저).
/// </summary>
public sealed class ReedSolomonCodec
{
    public const int SymbolsPerCodeword = 255;
    public const int DataSymbolsPerCodeword = 223;
    public const int ParitySymbolsPerCodeword = SymbolsPerCodeword - DataSymbolsPerCodeword;

    /// <summary>정정 가능한 심볼 수 t = 패리티/2.</summary>
    public const int CorrectableSymbols = ParitySymbolsPerCodeword / 2;

    private static readonly byte[] Generator = BuildGenerator();

    public ReedSolomonCodec(int interleavingDepth = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interleavingDepth, 1);
        InterleavingDepth = interleavingDepth;
    }

    public int InterleavingDepth { get; }

    /// <summary>부호화 뒤 코드블록 길이 (바이트).</summary>
    public int CodeblockLength => SymbolsPerCodeword * InterleavingDepth;

    /// <summary>코드블록에 담기는 데이터 길이 (바이트).</summary>
    public int DataLength => DataSymbolsPerCodeword * InterleavingDepth;

    /// <summary>데이터를 부호화해 코드블록을 만든다. 인터리빙은 심볼 단위로 번갈아 넣는다.</summary>
    public byte[] Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length != DataLength)
        {
            throw new ArgumentException($"data must be {DataLength} bytes, got {data.Length}", nameof(data));
        }

        var codeblock = new byte[CodeblockLength];
        Span<byte> codeword = stackalloc byte[SymbolsPerCodeword];
        for (int lane = 0; lane < InterleavingDepth; lane++)
        {
            codeword.Clear();
            for (int i = 0; i < DataSymbolsPerCodeword; i++)
            {
                codeword[i] = data[(i * InterleavingDepth) + lane];
            }

            EncodeCodeword(codeword);
            for (int i = 0; i < SymbolsPerCodeword; i++)
            {
                codeblock[(i * InterleavingDepth) + lane] = codeword[i];
            }
        }

        return codeblock;
    }

    /// <summary>코드블록을 복호해 데이터를 꺼낸다. 정정 불가를 만나면 즉시 실패로 돌려준다.</summary>
    public ReedSolomonResult Decode(ReadOnlySpan<byte> codeblock, Span<byte> data)
    {
        if (codeblock.Length != CodeblockLength)
        {
            throw new ArgumentException($"codeblock must be {CodeblockLength} bytes, got {codeblock.Length}", nameof(codeblock));
        }

        if (data.Length != DataLength)
        {
            throw new ArgumentException($"data must be {DataLength} bytes, got {data.Length}", nameof(data));
        }

        int corrected = 0;
        Span<byte> codeword = stackalloc byte[SymbolsPerCodeword];
        for (int lane = 0; lane < InterleavingDepth; lane++)
        {
            for (int i = 0; i < SymbolsPerCodeword; i++)
            {
                codeword[i] = codeblock[(i * InterleavingDepth) + lane];
            }

            if (!DecodeCodeword(codeword, out int fixedSymbols))
            {
                return new ReedSolomonResult(false, corrected);
            }

            corrected += fixedSymbols;
            for (int i = 0; i < DataSymbolsPerCodeword; i++)
            {
                data[(i * InterleavingDepth) + lane] = codeword[i];
            }
        }

        return new ReedSolomonResult(true, corrected);
    }

    /// <summary>g(x) = Π (x − α^j), j = 1 … 32. 계수는 높은 차수부터.</summary>
    private static byte[] BuildGenerator()
    {
        byte[] generator = [1];
        for (int j = 1; j <= ParitySymbolsPerCodeword; j++)
        {
            byte root = GaloisField256.Exp(j);
            var next = new byte[generator.Length + 1];
            for (int i = 0; i < generator.Length; i++)
            {
                next[i] ^= generator[i];
                next[i + 1] ^= GaloisField256.Multiply(generator[i], root);
            }

            generator = next;
        }

        return generator;
    }

    /// <summary>조직적 부호화 — 데이터는 그대로 두고 뒤 32 심볼에 나머지를 채운다.</summary>
    private static void EncodeCodeword(Span<byte> codeword)
    {
        Span<byte> data = stackalloc byte[DataSymbolsPerCodeword];
        codeword[..DataSymbolsPerCodeword].CopyTo(data);

        for (int i = 0; i < DataSymbolsPerCodeword; i++)
        {
            byte coefficient = codeword[i];
            if (coefficient == 0)
            {
                continue;
            }

            for (int j = 1; j < Generator.Length; j++)
            {
                codeword[i + j] ^= GaloisField256.Multiply(Generator[j], coefficient);
            }
        }

        // 나눗셈이 앞쪽(몫 자리)을 헤집었으므로 데이터를 되돌린다. 뒤 32 심볼이 나머지 = 패리티다.
        data.CopyTo(codeword[..DataSymbolsPerCodeword]);
    }

    /// <summary>신드롬 → Berlekamp-Massey → Chien → Forney. 정정에 실패하면 false.</summary>
    private static bool DecodeCodeword(Span<byte> codeword, out int corrected)
    {
        corrected = 0;
        Span<byte> syndromes = stackalloc byte[ParitySymbolsPerCodeword];
        bool clean = true;
        for (int j = 0; j < ParitySymbolsPerCodeword; j++)
        {
            byte x = GaloisField256.Exp(j + 1);
            byte value = 0;
            for (int i = 0; i < SymbolsPerCodeword; i++)
            {
                value = (byte)(GaloisField256.Multiply(value, x) ^ codeword[i]);
            }

            syndromes[j] = value;
            if (value != 0)
            {
                clean = false;
            }
        }

        if (clean)
        {
            return true;
        }

        byte[] lambda = BerlekampMassey(syndromes, out int errorCount);
        if (errorCount == 0 || errorCount > CorrectableSymbols)
        {
            return false;
        }

        Span<int> positions = stackalloc int[CorrectableSymbols];
        int found = 0;
        for (int exponent = 0; exponent < SymbolsPerCodeword; exponent++)
        {
            // Λ(α^-exponent) == 0 이면 x^exponent 자리에 오류가 있다.
            if (Evaluate(lambda, GaloisField256.Exp(-exponent)) != 0)
            {
                continue;
            }

            // 근이 errorCount 보다 많을 수는 없다 — 체에서 다항식의 근 개수는 차수를 넘지 못하고,
            // Λ 의 차수가 곧 errorCount 다. 도달할 수 없는 가지는 두지 않는다.
            positions[found++] = exponent;
        }

        if (found != errorCount)
        {
            // 근의 개수가 차수와 다르면 정정 능력을 넘은 것 — 지어내지 않고 실패로 알린다.
            return false;
        }

        byte[] omega = ErrorEvaluator(syndromes, lambda);
        byte[] derivative = FormalDerivative(lambda);
        for (int k = 0; k < found; k++)
        {
            byte inverseRoot = GaloisField256.Exp(-positions[k]);

            // 근이 서로 다르면 형식 미분은 그 근에서 0 이 되지 않는다. 근이 겹치는 Λ 였다면
            // Chien 이 errorCount 보다 적게 찾아 바로 위에서 이미 실패로 빠졌다 — 0 검사는 도달 불가다.
            byte denominator = Evaluate(derivative, inverseRoot);
            byte magnitude = GaloisField256.Divide(Evaluate(omega, inverseRoot), denominator);
            int index = SymbolsPerCodeword - 1 - positions[k];
            codeword[index] ^= magnitude;
        }

        corrected = found;
        return true;
    }

    /// <summary>오류 위치 다항식 Λ(x) 를 찾는다. 계수는 낮은 차수부터, Λ[0] = 1.</summary>
    private static byte[] BerlekampMassey(ReadOnlySpan<byte> syndromes, out int errorCount)
    {
        var current = new byte[ParitySymbolsPerCodeword + 1];
        var previous = new byte[ParitySymbolsPerCodeword + 1];
        current[0] = 1;
        previous[0] = 1;
        int length = 0;
        int shift = 1;
        byte lastDiscrepancy = 1;

        for (int n = 0; n < ParitySymbolsPerCodeword; n++)
        {
            byte discrepancy = syndromes[n];
            for (int i = 1; i <= length; i++)
            {
                discrepancy ^= GaloisField256.Multiply(current[i], syndromes[n - i]);
            }

            if (discrepancy == 0)
            {
                shift++;
                continue;
            }

            byte scale = GaloisField256.Divide(discrepancy, lastDiscrepancy);
            var updated = (byte[])current.Clone();
            for (int i = 0; i + shift < updated.Length; i++)
            {
                updated[i + shift] ^= GaloisField256.Multiply(scale, previous[i]);
            }

            if (2 * length <= n)
            {
                previous = current;
                lastDiscrepancy = discrepancy;
                length = n + 1 - length;
                shift = 1;
            }
            else
            {
                shift++;
            }

            current = updated;
        }

        errorCount = length;
        return current;
    }

    /// <summary>Ω(x) = [S(x)·Λ(x)] mod x^32.</summary>
    private static byte[] ErrorEvaluator(ReadOnlySpan<byte> syndromes, byte[] lambda)
    {
        var omega = new byte[ParitySymbolsPerCodeword];
        for (int i = 0; i < ParitySymbolsPerCodeword; i++)
        {
            byte sum = 0;
            for (int j = 0; j <= i && j < lambda.Length; j++)
            {
                sum ^= GaloisField256.Multiply(lambda[j], syndromes[i - j]);
            }

            omega[i] = sum;
        }

        return omega;
    }

    /// <summary>GF(2^m) 에서 형식 미분 — 짝수 차수 항은 사라진다.</summary>
    private static byte[] FormalDerivative(byte[] polynomial)
    {
        var derivative = new byte[Math.Max(1, polynomial.Length - 1)];
        for (int i = 1; i < polynomial.Length; i += 2)
        {
            derivative[i - 1] = polynomial[i];
        }

        return derivative;
    }

    /// <summary>계수가 낮은 차수부터인 다항식을 x 에서 평가한다.</summary>
    private static byte Evaluate(byte[] polynomial, byte x)
    {
        byte value = 0;
        for (int i = polynomial.Length - 1; i >= 0; i--)
        {
            value = (byte)(GaloisField256.Multiply(value, x) ^ polynomial[i]);
        }

        return value;
    }
}
