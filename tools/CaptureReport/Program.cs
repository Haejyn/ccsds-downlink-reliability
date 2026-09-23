using System.Globalization;
using SpaceLink;
using SpaceLink.ChannelCoding;

// EIRSAT-1 고정 자료를 이 저장소의 수신 체인으로 복호해 CADU 마다 결과를 CSV 로 낸다 — README 그림(tools/make_readme_figures.py)의 입력.
// 판정은 RealCaptureTests 와 같다: RS 성공 여부, 정정 심볼 수, 같은 녹음을 SatNOGS 지상국이 복호한 프레임과 같은지.
string golden = args.Length > 0 ? args[0] : Path.Combine("tests", "SpaceLink.Tests", "golden");
byte[] stream = File.ReadAllBytes(Path.Combine(golden, "eirsat1_cadus.bin"));
byte[] satnogs = File.ReadAllBytes(Path.Combine(golden, "eirsat1_satnogs_frames.bin"));
const int FrameLength = 892;
var reference = Enumerable.Range(0, satnogs.Length / FrameLength)
    .Select(i => Convert.ToHexString(satnogs, i * FrameLength, FrameLength)).ToHashSet();

var codec = new ChannelCodec(FrameLength, 4, sequence: PseudorandomSequence.Legacy255);
Console.WriteLine("index,status,corrected,master_count");
int index = 0;
foreach (byte[] codeblock in new FrameSynchronizer(codec.CodeblockLength).Process(stream))
{
    ChannelDecodeResult result = codec.DecodeCodeblock(codeblock);
    string status = !result.Succeeded ? "failed"
        : reference.Contains(Convert.ToHexString(result.TransferFrame!)) ? "satnogs" : "extra";
    string count = result.Succeeded ? result.TransferFrame![2].ToString(CultureInfo.InvariantCulture) : "";
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{index++},{status},{result.CorrectedSymbols},{count}"));
}
