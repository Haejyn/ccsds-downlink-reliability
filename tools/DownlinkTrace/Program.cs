using System.Buffers.Binary;
using System.Globalization;
using SpaceLink;
using SpaceLink.ChannelCoding;

// Astrocast 0.1 고정 비트열을 이 저장소의 수신 체인으로 복호해 CADU 마다 결과를 CSV 로 낸다 — README 애니메이션(scripts/make_downlink_animation.py)의 입력.
// 판정은 RealCaptureTests 와 같다: 동기기 → RS(인터리빙 5, 이중 기저, PN 255) → 전송 프레임 검증(CRC-16).
// 동기기는 위치를 내놓지 않으므로 스트림을 한 바이트씩 넣어 코드블록이 나온 바이트를 기록하고,
// 그 바로 앞에서 내보낸 코드블록과 비트 단위로 똑같은 자리를 찾아 ASM 시작 비트로 적는다(복호 결과는 건드리지 않는다).
string golden = args.Length > 0 ? args[0] : Path.Combine("tests", "SpaceLink.Tests", "golden");
byte[] stream = File.ReadAllBytes(Path.Combine(golden, "astrocast_9k6_bits.bin"));
const int FrameLength = 1115;
const int Depth = 5;
var codec = new ChannelCodec(FrameLength, Depth, sequence: PseudorandomSequence.Legacy255);
var frameConfig = new FrameConfig(FrameLength, hasOperationalControlField: true, hasFrameErrorControl: true);
var synchronizer = new FrameSynchronizer(codec.CodeblockLength);
int caduBits = codec.CaduLength * 8;

Console.WriteLine("index,asm_bit,asm_bit_errors,emitted_after_byte,rs_ok,corrected,frame_check,scid,vcid,mc_count,vc_count,fhp,crc,head_hex");
int index = 0;
for (int i = 0; i < stream.Length; i++)
{
    foreach (byte[] codeblock in synchronizer.Process(stream.AsSpan(i, 1)))
    {
        long end = (i + 1L) * 8;
        long asmBit = -1;
        for (long p = end - caduBits; p >= Math.Max(0, end - caduBits - 8) && asmBit < 0; p--)
        {
            if (Matches(stream, p + 32, codeblock))
            {
                asmBit = p;
            }
        }

        int asmErrors = asmBit < 0 ? -1 : System.Numerics.BitOperations.PopCount(ReadBits(stream, asmBit, 32) ^ FrameSynchronizer.AttachedSyncMarker);
        ChannelDecodeResult channel = codec.DecodeCodeblock(codeblock);
        string check = "", scid = "", vcid = "", mc = "", vc = "", fhp = "", crc = "", head = "";
        if (channel.Succeeded)
        {
            byte[] tf = channel.TransferFrame!;
            FrameDecodeResult frame = TransferFrame.Decode(tf, frameConfig);
            check = frame.Error.ToString();
            crc = BinaryPrimitives.ReadUInt16BigEndian(tf.AsSpan(FrameLength - 2)).ToString("X4", CultureInfo.InvariantCulture);
            head = Convert.ToHexString(tf, 0, 16);
            if (frame.IsValid)
            {
                scid = frame.Frame!.SpacecraftId.ToString(CultureInfo.InvariantCulture);
                vcid = frame.Frame.VirtualChannelId.ToString(CultureInfo.InvariantCulture);
                mc = frame.Frame.MasterChannelFrameCount.ToString(CultureInfo.InvariantCulture);
                vc = frame.Frame.VirtualChannelFrameCount.ToString(CultureInfo.InvariantCulture);
                fhp = frame.Frame.FirstHeaderPointerValue.ToString("X3", CultureInfo.InvariantCulture);
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{index++},{asmBit},{asmErrors},{i},{channel.Succeeded},{channel.CorrectedSymbols},{check},{scid},{vcid},{mc},{vc},{fhp},{crc},{head}"));
    }
}

static bool Matches(byte[] stream, long bit, byte[] expected)
{
    if (bit < 0 || bit + (expected.Length * 8L) > stream.Length * 8L)
    {
        return false;
    }

    for (int k = 0; k < expected.Length; k++)
    {
        if (ReadBits(stream, bit + (k * 8L), 8) != expected[k])
        {
            return false;
        }
    }

    return true;
}

static uint ReadBits(byte[] stream, long bit, int count)
{
    uint value = 0;
    for (int k = 0; k < count; k++)
    {
        long b = bit + k;
        value = (value << 1) | (uint)((stream[b >> 3] >> (7 - (int)(b & 7))) & 1);
    }

    return value;
}
