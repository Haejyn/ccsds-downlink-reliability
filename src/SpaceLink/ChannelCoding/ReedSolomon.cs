namespace SpaceLink.ChannelCoding;

/// <summary>복호 결과 — 성공 여부와 정정한 심볼 수.</summary>
/// <remarks>
/// <see cref="Succeeded"/> 가 true 라고 해서 원본이 복원됐다는 뜻은 아니다. 정정 능력을 넘는 오류에서는
/// 복호기가 "성공" 이라 말하면서 **다른 부호어로 잘못 정정**할 수 있다 (오정정). 그 비율은 시험에서 실측한다.
/// </remarks>
public readonly record struct ReedSolomonResult(bool Succeeded, int CorrectedSymbols);

/// <summary>
/// 리드-솔로몬 RS(255,223) — 심볼 255 개 중 데이터 223, 패리티 32, 정정 능력 16 심볼.
/// 인터리빙 깊이 I 를 쓰면 코드블록은 255·I 바이트(가상 채움 Q 를 쓰면 255·I − Q)가 되고, 연속 버스트가 부호어 여러 개로 흩어진다.
///
/// CCSDS 131.0-B-5 의 표준 RS 코드다 — 아래 두 가지를 표준 문서(§4.3.4, §4.3.9, 부속서 F·G)로 확인했다.
///
/// **부호 생성 다항식의 근** — g(x) = Π (x − β^n), n = 112…143, β = α^11 (§4.3.4, E=16 이라 j = 128−E…127+E).
/// α^1…α^32 (관례 기저의 "가장 쉬운" 선택) 이 아니다. 부속서 G 가 싣는 계수표(G0…G32, 32 개 전부)와
/// 이 근으로 직접 계산한 값이 지수 단위로 정확히 일치한다 — 시험이 그 표를 그대로 대조한다.
///
/// **이중 기저(dual basis)** — 부호 계산은 관례 기저로 하고, 전송하는 바이트는 <see cref="DualBasisTransform"/> 이
/// 담당하는 이중 기저다(§4.3.9, 부속서 F). 정보 심볼은 전송 바이트 그대로(계산 전에 관례로 바꿨다가 계산 뒤
/// 다시 이중으로 돌리면 항등이라 원본을 그대로 쓴다) 나가고, 새로 계산한 패리티만 변환해 내보낸다.
///
/// **Forney 공식의 보정** — 근이 β^0 이 아니라 β^112 에서 시작하므로(FCR = 112), 오류 크기 계산에
/// X_k^(1−FCR) 인수가 더 필요하다. FCR = 1(교과서에서 흔한 α^1…α^2t 관례, 이 코드가 전에 쓰던 것)이면
/// 이 인수가 1 이 되어 사라진다 — 그래서 이전 구현은 이 인수 없이도 맞았다.
///
/// **짧은 코드블록(가상 채움)** — 전송 프레임이 223·I 보다 짧으면 코드블록 **앞쪽** Q 심볼을 0 으로 두고
/// 부호화하되, 그 0 은 **보내지 않는다**(§4.3.7, Q 는 I 의 배수). 복호기는 같은 자리에 0 을 되살려 복호하고,
/// 오류 위치가 채움 자리로 나오면 **실패로 알린다** — 그 자리는 0 인 게 확실하니 그리로 "정정" 하는 것은
/// 다른 부호어로 잘못 가는 것이다.
///
/// 공개 구현 libfec(Phil Karn)의 CCSDS RS 실제 출력과 바이트 단위로 대조한다 — 부호화 35 개가 같고, 복호 판정도 같다.
/// 다만 libfec 는 채움 자리로 가는 정정을 건너뛰고 성공으로 알리는데, 이 구현은 실패로 알린다(<c>LibfecCrossCheckTests</c>).
///
/// 실제 위성(Astrocast 0.1) 캡처의 코드블록(I = 5)도 복호하고, 송신기가 계산한 프레임 CRC 가 맞는다(<c>RealCaptureTests</c>).
/// </summary>
public sealed class ReedSolomonCodec
{
    public const int SymbolsPerCodeword = 255;
    public const int DataSymbolsPerCodeword = 223;
    public const int ParitySymbolsPerCodeword = SymbolsPerCodeword - DataSymbolsPerCodeword;

    /// <summary>정정 가능한 심볼 수 t = 패리티/2.</summary>
    public const int CorrectableSymbols = ParitySymbolsPerCodeword / 2;

    /// <summary>근의 간격 β = α^11 (§4.3.4).</summary>
    internal const int RootSpacing = 11;

    /// <summary>첫 근의 β 지수 — E=16 이면 j = 128−E = 112 부터 32 개(§4.3.4).</summary>
    internal const int FirstConsecutiveRoot = 128 - CorrectableSymbols;

    /// <summary>
    /// 표준이 허용하는 인터리빙 깊이 — I = 1, 2, 3, 4, 5, 8 (§4.3.5.1). 그 밖의 값도 산술로는 돌아가지만,
    /// 표준 수신기와 맞출 수 없는 코드블록이 된다(예: I = 6 이면 1530 바이트 — 어떤 표준 링크에도 없는 길이).
    /// </summary>
    public static IReadOnlyList<int> AllowedInterleavingDepths { get; } = [1, 2, 3, 4, 5, 8];

    private static readonly byte[] Generator = BuildGenerator();

    /// <param name="interleavingDepth">인터리빙 깊이 I — <see cref="AllowedInterleavingDepths"/> 중 하나.</param>
    /// <param name="virtualFill">
    /// 가상 채움 Q (심볼) — 코드블록 앞쪽에서 0 으로 치고 보내지 않는 심볼 수. I 의 배수이고 223·I 보다 작아야 한다(§4.3.7.3).
    /// </param>
    public ReedSolomonCodec(int interleavingDepth = 1, int virtualFill = 0)
    {
        ValidateInterleavingDepth(interleavingDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(virtualFill);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(virtualFill, DataSymbolsPerCodeword * interleavingDepth);
        if (virtualFill % interleavingDepth != 0)
        {
            throw new ArgumentException(
                $"virtual fill ({virtualFill}) must be a multiple of the interleaving depth ({interleavingDepth})", nameof(virtualFill));
        }

        InterleavingDepth = interleavingDepth;
        VirtualFill = virtualFill;
    }

    public int InterleavingDepth { get; }

    internal static void ValidateInterleavingDepth(int interleavingDepth)
    {
        if (!AllowedInterleavingDepths.Contains(interleavingDepth))
        {
            throw new ArgumentOutOfRangeException(nameof(interleavingDepth), interleavingDepth,
                $"interleaving depth must be one of {string.Join(", ", AllowedInterleavingDepths)} (CCSDS 131.0-B-5 §4.3.5.1)");
        }
    }

    /// <summary>가상 채움 Q — 코드블록 앞쪽의 보내지 않는 0 심볼 수.</summary>
    public int VirtualFill { get; }

    /// <summary>부호화 뒤 (전송되는) 코드블록 길이 (바이트).</summary>
    public int CodeblockLength => (SymbolsPerCodeword * InterleavingDepth) - VirtualFill;

    /// <summary>코드블록에 담기는 데이터 길이 (바이트).</summary>
    public int DataLength => (DataSymbolsPerCodeword * InterleavingDepth) - VirtualFill;

    /// <summary>부호어 하나(한 레인)에서 가상 채움이 차지하는 앞쪽 심볼 수 = Q / I.</summary>
    private int FillPerCodeword => VirtualFill / InterleavingDepth;

    /// <summary>n 번째(0-index) 부호 근 β^(112+n) = α^(11·(112+n)). n = 0 … 31.</summary>
    internal static byte RootAt(int n) => GaloisField256.Exp(RootSpacing * (FirstConsecutiveRoot + n));

    /// <summary>데이터를 부호화해 코드블록을 만든다. 인터리빙은 심볼 단위로 번갈아 넣는다.</summary>
    public byte[] Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length != DataLength)
        {
            throw new ArgumentException($"data must be {DataLength} bytes, got {data.Length}", nameof(data));
        }

        var codeblock = new byte[CodeblockLength];
        Span<byte> codeword = stackalloc byte[SymbolsPerCodeword];
        int fill = FillPerCodeword;
        for (int lane = 0; lane < InterleavingDepth; lane++)
        {
            // 앞쪽 fill 심볼은 가상 채움 — 0 으로 두고 부호화한다(0 은 두 기저 모두에서 0 이다).
            codeword.Clear();
            for (int i = fill; i < DataSymbolsPerCodeword; i++)
            {
                // 전송(이중 기저) 그대로 받은 정보 심볼을, 부호화 대수(관례 기저)를 하기 전에 바꾼다.
                codeword[i] = DualBasisTransform.ToConventional(data[TransmittedIndex(i, lane)]);
            }

            EncodeCodeword(codeword);

            for (int i = fill; i < DataSymbolsPerCodeword; i++)
            {
                // 정보 심볼은 원본 그대로 내보낸다 — 관례로 바꿨다 다시 이중으로 되돌리면 항등이다.
                codeblock[TransmittedIndex(i, lane)] = data[TransmittedIndex(i, lane)];
            }

            for (int i = DataSymbolsPerCodeword; i < SymbolsPerCodeword; i++)
            {
                // 새로 계산한 패리티만 이중 기저로 바꿔 내보낸다.
                codeblock[TransmittedIndex(i, lane)] = DualBasisTransform.ToDualBasis(codeword[i]);
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
        int fill = FillPerCodeword;
        for (int lane = 0; lane < InterleavingDepth; lane++)
        {
            // 보내지 않은 가상 채움 자리(앞 fill 칸)는 채우지 않는다 — 0 이 확실한 자리라 신드롬·Chien·출력 어디서도
            // 읽지 않는다(신드롬은 fill 부터, Chien 은 보낸 자리만, 출력은 fill 부터). 0 으로 지우는 줄을 두었다가 생존 뮤턴트가
            // 아무도 읽지 않는다는 것을 가리켜 지웠다.
            for (int i = fill; i < SymbolsPerCodeword; i++)
            {
                // 오류가 정보 심볼에 있든 패리티에 있든, 부호화 대수는 전부 관례 기저에서 한다.
                codeword[i] = DualBasisTransform.ToConventional(codeblock[TransmittedIndex(i, lane)]);
            }

            if (!DecodeCodeword(codeword, fill, out int fixedSymbols))
            {
                return new ReedSolomonResult(false, corrected);
            }

            corrected += fixedSymbols;
            for (int i = fill; i < DataSymbolsPerCodeword; i++)
            {
                // 정정된 정보 심볼을 원래 표현(이중 기저)으로 되돌린다.
                data[TransmittedIndex(i, lane)] = DualBasisTransform.ToDualBasis(codeword[i]);
            }
        }

        return new ReedSolomonResult(true, corrected);
    }

    /// <summary>
    /// 레인 lane 의 부호어 심볼 i 가 전송 코드블록(또는 데이터)에서 놓이는 자리.
    /// 채움이 없으면 i·I + lane 이고, 앞쪽 Q = fill·I 자리가 빠진 만큼 당겨진다 (§4.3.7.4).
    /// </summary>
    private int TransmittedIndex(int i, int lane) => ((i - FillPerCodeword) * InterleavingDepth) + lane;

    /// <summary>g(x) = Π (x − β^(112+n)), n = 0 … 31. 계수는 높은 차수부터.</summary>
    private static byte[] BuildGenerator()
    {
        byte[] generator = [1];
        for (int n = 0; n < ParitySymbolsPerCodeword; n++)
        {
            byte root = RootAt(n);
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

    /// <summary>
    /// 조직적 부호화의 나눗셈 — 뒤 32 심볼에 나머지(= 패리티)를 채운다. 앞 223 심볼은 나눗셈이 몫 자리로 헤집어 놓는다.
    /// 예전에는 데이터를 저장했다 되돌렸지만, A4 부터 <see cref="Encode"/> 가 정보 심볼을 입력에서 바로 내보내
    /// 그 복원을 읽는 곳이 없어졌다(생존 뮤턴트 두 개가 가리켰다) — 호출자는 패리티만 읽는다.
    /// </summary>
    private static void EncodeCodeword(Span<byte> codeword)
    {
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

    }

    /// <summary>
    /// 신드롬 → Berlekamp-Massey → Chien → Forney. 정정에 실패하면 false.
    /// 앞쪽 fill 심볼은 가상 채움(0 이 확실한 자리)이다.
    /// </summary>
    private static bool DecodeCodeword(Span<byte> codeword, int fill, out int corrected)
    {
        corrected = 0;
        Span<byte> syndromes = stackalloc byte[ParitySymbolsPerCodeword];
        bool clean = true;
        for (int n = 0; n < ParitySymbolsPerCodeword; n++)
        {
            byte x = RootAt(n);
            byte value = 0;

            // Horner 는 높은 차수부터 곱해 내려온다 — 앞쪽 채움(0)은 value 를 0 으로 둔 채 지나가므로 건너뛰어도 같다.
            // 신드롬 계산이 수신 체인 시간의 대부분이라, 128 바이트 프레임(채움 95 심볼)이면 곱셈이 37 % 준다.
            for (int i = fill; i < SymbolsPerCodeword; i++)
            {
                value = (byte)(GaloisField256.Multiply(value, x) ^ codeword[i]);
            }

            syndromes[n] = value;
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
        // 전송된 자리(인덱스 ≥ fill, 곧 exponent ≤ 254 − fill)에서만 찾는다. 근이 채움 자리에 있으면 여기서 못 찾아
        // found 가 errorCount 보다 작아지고 아래에서 실패로 빠진다 — 0 이 확실한 자리로 "정정" 하면 다른 부호어로 가는 것이다.
        for (int exponent = 0; exponent < SymbolsPerCodeword - fill; exponent++)
        {
            // Λ(β^-exponent) == 0 이면 x^exponent 자리에 오류가 있다. β = α^11 이 근의 간격이다.
            if (Evaluate(lambda, GaloisField256.Exp(-RootSpacing * exponent)) != 0)
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
            byte inverseRoot = GaloisField256.Exp(-RootSpacing * positions[k]);

            // 근이 서로 다르면 형식 미분은 그 근에서 0 이 되지 않는다. 근이 겹치는 Λ 였다면
            // Chien 이 errorCount 보다 적게 찾아 바로 위에서 이미 실패로 빠졌다 — 0 검사는 도달 불가다.
            byte denominator = Evaluate(derivative, inverseRoot);
            byte magnitude = GaloisField256.Divide(Evaluate(omega, inverseRoot), denominator);

            // 근이 β^0 이 아니라 β^112 부터 시작해(FCR=112) X_k^(1−FCR) 인수가 더 필요하다 —
            // 교과서 관례(FCR=1, 이 코드가 전에 쓰던 α^1…α^32)면 인수가 1 이 되어 사라진다.
            // X_k = β^exponent = α^(11·exponent) 이므로 인수는 α^(11·exponent·(1−112)) 다.
            int scaleExponent = RootSpacing * positions[k] * (1 - FirstConsecutiveRoot);
            magnitude = GaloisField256.Multiply(magnitude, GaloisField256.Exp(scaleExponent));

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

    /// <summary>
    /// Ω(x) = [S(x)·Λ(x)] mod x^32. Λ 는 언제나 <c>ParitySymbolsPerCodeword + 1</c> 칸이다(<see cref="BerlekampMassey"/>) —
    /// 그래서 안쪽 합의 상한이 <c>j &lt;= i</c> 하나로 충분하다 (i ≤ 31 &lt; 33).
    /// </summary>
    private static byte[] ErrorEvaluator(ReadOnlySpan<byte> syndromes, byte[] lambda)
    {
        var omega = new byte[ParitySymbolsPerCodeword];
        for (int i = 0; i < ParitySymbolsPerCodeword; i++)
        {
            byte sum = 0;
            for (int j = 0; j <= i; j++)
            {
                sum ^= GaloisField256.Multiply(lambda[j], syndromes[i - j]);
            }

            omega[i] = sum;
        }

        return omega;
    }

    /// <summary>
    /// GF(2^m) 에서 형식 미분 — 짝수 차수 항은 사라진다. 입력은 언제나 33 칸의 Λ 이므로 결과는 32 칸이다.
    /// </summary>
    private static byte[] FormalDerivative(byte[] polynomial)
    {
        var derivative = new byte[ParitySymbolsPerCodeword];
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
