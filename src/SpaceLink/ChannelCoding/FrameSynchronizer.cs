namespace SpaceLink.ChannelCoding;

/// <summary>프레임 동기 상태기계의 상태.</summary>
public enum SyncState
{
    /// <summary>비트 단위로 동기 마커를 찾는 중.</summary>
    Search,

    /// <summary>후보를 찾았고, 다음 마커가 제자리에 오는지 확인하는 중.</summary>
    Check,

    /// <summary>잠금 — 경계를 믿고 코드블록을 내보낸다.</summary>
    Lock,

    /// <summary>마커를 놓쳤지만 아직 관성으로 유지하는 중 (flywheel).</summary>
    Flywheel,
}

/// <summary>
/// 동기 마커(ASM) 탐색 — 수신 비트열에는 바이트 경계도 프레임 경계도 없다.
/// 0x1ACFFC1D 를 **비트 단위로** 찾아 코드블록 경계를 세우고, search → check → lock → flywheel 로 관리한다.
///
/// 잠금 뒤에는 마커가 조금 망가져도 계속 내보낸다 (flywheel). 연속으로 허용치를 넘게 놓치면 다시 탐색으로 돌아간다.
/// 비트 슬립(비트가 끼거나 빠짐)에서 몇 코드블록 만에 다시 잠기는지는 시험에서 실측한다.
/// </summary>
public sealed class FrameSynchronizer
{
    /// <summary>CCSDS 부착 동기 마커.</summary>
    public const uint AttachedSyncMarker = 0x1ACFFC1D;

    /// <summary>마커 길이 (바이트).</summary>
    public const int MarkerLength = 4;

    private readonly int _codeblockLength;
    private readonly int _lockThreshold;
    private readonly int _flywheelTolerance;
    private readonly int _maxMarkerBitErrors;
    private readonly List<byte> _buffer = [];

    private long _consumedBits;      // _buffer 앞에서 이미 버린 비트 수
    private long _searchBit;         // 탐색 중 다음에 볼 절대 비트 위치
    private long _blockStartBit;     // 현재 코드블록(마커 포함)이 시작하는 절대 비트 위치
    private long _candidateStartBit; // 탐색이 찾아낸 후보 마커 자리 (헛짚었을 때 여기서부터 다시 찾는다)
    private int _goodMarkers;
    private int _missedInARow;

    /// <param name="codeblockLength">마커를 뺀 코드블록 길이 (바이트).</param>
    /// <param name="lockThreshold">잠금으로 넘어가는 데 필요한 연속 마커 수.</param>
    /// <param name="flywheelTolerance">잠금 뒤 연속으로 놓쳐도 버티는 마커 수.</param>
    /// <param name="maxMarkerBitErrors">마커로 인정하는 최대 비트 오류 수 (해밍 거리).</param>
    public FrameSynchronizer(int codeblockLength, int lockThreshold = 2, int flywheelTolerance = 3, int maxMarkerBitErrors = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(codeblockLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(lockThreshold, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(flywheelTolerance);
        ArgumentOutOfRangeException.ThrowIfNegative(maxMarkerBitErrors);
        _codeblockLength = codeblockLength;
        _lockThreshold = lockThreshold;
        _flywheelTolerance = flywheelTolerance;
        _maxMarkerBitErrors = maxMarkerBitErrors;
    }

    public SyncState State { get; private set; } = SyncState.Search;

    /// <summary>내보낸 코드블록 수.</summary>
    public long CodeblocksEmitted { get; private set; }

    /// <summary>잠금 뒤 제자리에 없던 마커 수.</summary>
    public long MarkersMissed { get; private set; }

    /// <summary>잠금을 잃고 다시 탐색으로 돌아간 횟수.</summary>
    public long Resyncs { get; private set; }

    /// <summary>
    /// 아직 소비하지 않고 보유 중인 입력 바이트 수 — 경계를 확인하려고 들고 있어야 하는 만큼이다.
    /// 스트림이 아무리 길어도 CADU 한 장 안쪽으로 유지된다(REQ-ASM-04). 구현이 아니라 <b>유계인가</b> 가 계약이다:
    /// 정확한 값은 증가 정책에 따라 달라질 수 있으니 상한만 믿는다. 수신 백로그로 운용 텔레메트리에도 쓴다.
    /// </summary>
    public int BufferedBytes => _buffer.Count;

    /// <summary>스트림 조각을 넣고, 경계가 맞은 코드블록을 돌려준다 (마커 제외).</summary>
    public List<byte[]> Process(ReadOnlySpan<byte> stream)
    {
        _buffer.AddRange(stream);
        var blocks = new List<byte[]>();
        int caduBits = (MarkerLength + _codeblockLength) * 8;

        while (true)
        {
            long totalBits = _consumedBits + ((long)_buffer.Count * 8);
            if (State == SyncState.Search)
            {
                if (!TrySearch(totalBits))
                {
                    break;
                }

                continue;
            }

            if (totalBits - _blockStartBit < caduBits)
            {
                break;
            }

            bool markerPresent = HammingDistance(ReadUInt32(_blockStartBit), AttachedSyncMarker) <= _maxMarkerBitErrors;
            if (!markerPresent && State == SyncState.Check)
            {
                // 헛짚었다 — 후보 마커 **바로 다음 비트**부터 다시 찾는다.
                // 실패한 경계에서 이으면 후보와 그 경계 사이에 있는 진짜 마커를 건너뛴다.
                State = SyncState.Search;
                _searchBit = _candidateStartBit + 1;
                continue;
            }

            if (markerPresent)
            {
                _missedInARow = 0;
                _goodMarkers++;
                if (State != SyncState.Lock && _goodMarkers >= _lockThreshold)
                {
                    State = SyncState.Lock;
                }
            }
            else
            {
                MarkersMissed++;
                _missedInARow++;
                State = SyncState.Flywheel;
                if (_missedInARow > _flywheelTolerance)
                {
                    Resyncs++;
                    State = SyncState.Search;
                    _goodMarkers = 0;
                    _missedInARow = 0;
                    _searchBit = _blockStartBit + 1;
                    continue;
                }
            }

            blocks.Add(ReadBytes(_blockStartBit + (MarkerLength * 8), _codeblockLength));
            CodeblocksEmitted++;
            _blockStartBit += caduBits;
        }

        // 버퍼는 이 호출의 끝에서 **한 번만** 줄인다. Process 안에서는 AddRange 뒤로 버퍼가 줄기만 하므로
        // 루프 안에서 잘라도 최대 점유는 그대로이고, 코드블록마다 RemoveRange 가 남은 전체를 앞으로 당겨
        // 한 번에 큰 스트림을 넣으면 제곱 시간이 된다. (예전에는 여기·루프 안·TrySearch 끝 세 곳에서 불렀다 —
        // 서로 중복이라 하나를 지워도 나머지가 덮어 주어 어떤 시험으로도 죽지 않았다.)
        Trim();
        return blocks;
    }

    private bool TrySearch(long totalBits)
    {
        while (_searchBit + 32 <= totalBits)
        {
            if (HammingDistance(ReadUInt32(_searchBit), AttachedSyncMarker) <= _maxMarkerBitErrors)
            {
                _blockStartBit = _searchBit;
                _candidateStartBit = _searchBit;

                // 찾은 마커는 여기서 세지 않는다 — 경계에서 다시 확인될 때 센다.
                // 1 로 두면 본 루프가 같은 마커를 한 번 더 세어 lockThreshold 와 무관하게
                // **첫 마커 하나에 바로 잠긴다** (Check 가 이름뿐이 된다). 실제로 그렇게 만들었다가
                // 가짜 마커를 버리는 시험이 잡아냈다.
                _goodMarkers = 0;
                _missedInARow = 0;
                State = SyncState.Check;
                return true;
            }

            _searchBit++;
        }

        return false;
    }

    /// <summary>더 볼 일이 없는 앞쪽 바이트를 버린다 — 스트림이 길어도 버퍼가 자라지 않게.</summary>
    private void Trim()
    {
        // Check 상태에서는 **후보 자리까지** 남겨 둔다 — 헛짚었을 때 그 자리 +1 비트부터 다시 찾기 때문이다.
        // 여기서 _blockStartBit(다음 경계)까지 버리면 되감을 자리가 사라져 ReadBit 이 버퍼 밖을 짚는다.
        long keepFrom = State switch
        {
            SyncState.Search => _searchBit,
            SyncState.Check => _candidateStartBit,
            _ => _blockStartBit,
        };

        // 불변식: keepFrom >= _consumedBits — 마지막 정리가 이미 그 자리 이전을 버렸고, keepFrom 은 뒤로 되감기지 않는다
        // (헛짚은 Check 는 후보 자리 +1 비트로, 놓침 초과 재동기는 경계 +1 비트로 Search 가 시작한다).
        // 그래서 dropBytes 는 음수가 될 수 없고, 0 이면 RemoveRange(0, 0) 이 아무 일도 하지 않는다.
        long dropBytes = (keepFrom - _consumedBits) / 8;
        _buffer.RemoveRange(0, (int)dropBytes);
        _consumedBits += dropBytes * 8;
    }

    private bool ReadBit(long bitIndex)
    {
        long offset = bitIndex - _consumedBits;
        int byteIndex = (int)(offset >> 3);
        int bitInByte = 7 - (int)(offset & 7);
        return ((_buffer[byteIndex] >> bitInByte) & 1) == 1;
    }

    private uint ReadUInt32(long bitIndex)
    {
        uint value = 0;
        for (int i = 0; i < 32; i++)
        {
            value = (value << 1) | (ReadBit(bitIndex + i) ? 1u : 0u);
        }

        return value;
    }

    private byte[] ReadBytes(long bitIndex, int count)
    {
        var bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            byte value = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (byte)((value << 1) | (ReadBit(bitIndex + ((long)i * 8) + bit) ? 1 : 0));
            }

            bytes[i] = value;
        }

        return bytes;
    }

    private static int HammingDistance(uint a, uint b) =>
        System.Numerics.BitOperations.PopCount(a ^ b);
}
