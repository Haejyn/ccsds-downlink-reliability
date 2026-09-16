#!/usr/bin/env python3
"""
생존 뮤턴트를 죽이는 시험을 모델에게 쓰게 하고, **뮤테이션 점수로 채택 여부를 판정하는** 루프.

  python tools/mutation_loop/loop.py --report build/stryker/reports/mutation-report.html
  python tools/mutation_loop/loop.py --report <html> --prompts bare,context,guarded --target PacketExtractor.cs

왜 이렇게 만드나
---------------
시험을 AI 에게 쓰게 하는 것 자체는 쉽다. 어려운 것은 **그 시험이 쓸모 있는지 누가 판정하는가** 다.
"보기에 괜찮다" 로 받으면 시험 수만 늘고 결함 검출력은 그대로다. 그래서 심판을 사람이 아니라
**이미 있는 지표**에 맡긴다 — 넣기 전과 후의 생존 뮤턴트 수. 줄지 않으면 버린다.

한 바퀴:
    생존 목록(축약)  →  프롬프트  →  시험 초안  →  컴파일·실행  →  대상 파일만 뮤테이션 재측정
                                                                    ↓
                                            생존이 줄면 채택, 아니면 되돌린다 (판정은 숫자가 한다)

지키는 선
---------
- **CI 에서 돌리지 않는다.** 모델을 반복 호출하므로 사람이 명시적으로 실행할 때만 돈다.
- **생성한 시험을 바로 커밋하지 않는다.** 채택된 것도 사람이 읽고 남길지 정한다 (§금지 목록 참고).
- 실패해도 저장소를 더럽히지 않는다 — 생성 시험은 한 파일에만 쓰고, 채택 안 되면 지운다.

금지 목록 (프롬프트에 그대로 넣는다)
-----------------------------------
- 예외·이벤트의 **메시지 문구를 단언하지 않는다.** 문구는 계약이 아니다 (docs/test-report.md §4).
- **구현 세부를 단언하지 않는다** (할당 횟수·버퍼 증가 정책 같은 것). 뮤턴트를 죽이려고 시험을
  구현에 붙들어 매면, 다음 리팩터에서 시험이 먼저 깨진다.
- 이 둘 때문에 **죽일 수 없는 뮤턴트가 남는 것은 정상이다.** 억지로 점수를 올리지 않는다.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
TEST_DIR = ROOT / "tests" / "SpaceLink.Tests"
GENERATED = TEST_DIR / "MutationLoopGeneratedTests.cs"

# 프롬프트 판 — 같은 생존 목록에 돌려 어느 판이 몇 개를 죽이는지 표로 남긴다.
PROMPTS: dict[str, str] = {
    # ① 맨몸: 변이 줄만 준다. 문맥이 얼마나 필요한지 보는 바닥선.
    "bare": """다음은 C# 프로젝트에서 살아남은 변이(mutant)다. 이 변이를 죽이는 xUnit 시험을 써라.

{survivors}

규칙:
- 시험 메서드만 출력한다. 클래스 선언·using·설명 금지.
- 메시지 문구를 단언하지 마라.
- 구현 세부(할당 횟수 등)를 단언하지 마라.
""",
    # ② 문맥: 변이 주변 코드까지.
    "context": """다음은 C# 프로젝트에서 살아남은 변이와 그 주변 코드다. 각 변이를 죽이는 xUnit 시험을 써라.
살아남았다는 것은 그 코드를 저렇게 바꿔도 **모든 시험이 통과한다**는 뜻이다.

{survivors}

이미 있는 시험 이름 (같은 이름을 쓰지 마라):
{existing}

규칙:
- 시험 메서드만 출력한다. 클래스 선언·using·설명 금지.
- 메시지 문구를 단언하지 마라 — 문구는 계약이 아니다.
- 구현 세부(할당 횟수·버퍼 증가 정책)를 단언하지 마라.
- 공개 API 로만 쓴다: {api}
""",
    # ③ 안내: ②에 "왜 안 잡혔는지 먼저 생각하라"를 더한다.
    "guarded": """다음은 C# 프로젝트에서 살아남은 변이와 그 주변 코드다.

{survivors}

이미 있는 시험 이름 (같은 이름을 쓰지 마라):
{existing}

각 변이마다 **먼저** 판단해라:
  (a) 바꿔도 **관찰 가능한 동작이 같다** → 죽일 수 없다. 건너뛴다.
  (b) 메시지 문구만 바뀐다 → 시험하지 않는다 (문구는 계약이 아니다).
  (c) 동작이 실제로 달라지는데 아무 시험도 그 차이를 보지 않는다 → **이것만** 시험을 쓴다.

(c) 에 해당하는 것에 대해서만 xUnit 시험 메서드를 써라. 하나도 없으면 아무것도 출력하지 마라.

규칙:
- 시험 메서드만 출력한다. 클래스 선언·using·설명 금지.
- 구현 세부(할당 횟수·버퍼 증가 정책)를 단언하지 마라.
- 공개 API 로만 쓴다: {api}
""",
}

# 이름만 나열하면 모델이 시그니처를 추측한다 — 실제 선언을 그대로 준다.
PUBLIC_API = """\
public sealed class FrameConfig { public FrameConfig(int frameLength, bool hasOperationalControlField = false, bool hasFrameErrorControl = true); public int FrameLength { get; } public int DataFieldLength { get; } public const int PrimaryHeaderLength = 6; public const int MaxDataFieldLength = 2046; }
public sealed class TransferFrame { public TransferFrame(ushort spacecraftId, byte virtualChannelId, byte masterChannelFrameCount, byte virtualChannelFrameCount, ushort firstHeaderPointer, ReadOnlySpan<byte> dataField, uint operationalControlField = 0); public byte[] Encode(FrameConfig config); public static FrameDecodeResult Decode(ReadOnlySpan<byte> bytes, FrameConfig config); }
public sealed class SpacePacket { public SpacePacket(ushort apid, ushort sequenceCount, ReadOnlySpan<byte> data, SequenceFlags sequenceFlags = SequenceFlags.Unsegmented, PacketType type = PacketType.Telemetry, bool hasSecondaryHeader = false); public byte[] Encode(); public static SpacePacket Decode(ReadOnlySpan<byte> bytes); public static SpacePacket WithErrorControl(ushort apid, ushort sequenceCount, ReadOnlySpan<byte> userData, SequenceFlags sequenceFlags = SequenceFlags.Unsegmented); public bool HasValidErrorControl(); public ReadOnlyMemory<byte> Data { get; } public const int PrimaryHeaderLength = 6; public const int MaxDataLength = 65536; }
public sealed class PacketExtractor { public PacketExtractor(FrameConfig config, bool verifyPacketErrorControl = false); public ExtractionResult Process(ReadOnlySpan<byte> rawFrame); }
public sealed record ExtractionResult(IReadOnlyList<SpacePacket> Packets, IReadOnlyList<LinkEvent> Events);
public sealed record LinkEvent(LinkEventKind Kind, int? VirtualChannelId, string Detail);
public sealed class FramePacker { public FramePacker(FrameConfig config, ushort spacecraftId, byte virtualChannelId, byte firstFrameCount = 0); public IReadOnlyList<TransferFrame> Pack(IEnumerable<SpacePacket> packets); public TransferFrame IdleFrame(); }
public static class Crc16Ccitt { public static ushort Compute(ReadOnlySpan<byte> data); public static ushort Compute(ReadOnlySpan<byte> data, ushort seed); }
public readonly record struct ReedSolomonResult(bool Succeeded, int CorrectedSymbols);
public sealed class ReedSolomonCodec { public ReedSolomonCodec(int interleavingDepth = 1); public int InterleavingDepth { get; } public int CodeblockLength { get; } public int DataLength { get; } public byte[] Encode(ReadOnlySpan<byte> data); public ReedSolomonResult Decode(ReadOnlySpan<byte> codeblock, Span<byte> data); public const int SymbolsPerCodeword = 255; public const int DataSymbolsPerCodeword = 223; public const int ParitySymbolsPerCodeword = 32; public const int CorrectableSymbols = 16; }

시험 파일에는 `using Xunit;` 과 `namespace SpaceLink.Tests;` 가 이미 있다. 위에 없는 형·멤버는 존재한다고 가정하지 마라."""


@dataclass
class Outcome:
    prompt: str
    generated: int = 0
    compiled: bool = False
    tests_passed: bool = False
    survivors_before: int = 0
    survivors_after: int = 0
    accepted: bool = False
    seconds: float = 0.0
    note: str = ""
    killed: list[str] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)


def dotnet_env() -> dict[str, str]:
    """dotnet 은 사용자 폴더에 설치돼 있다. 도구(stryker)도 그 런타임을 찾아야 한다."""
    env = dict(os.environ)
    root = Path(env.get("LOCALAPPDATA", "")) / "Microsoft" / "dotnet"
    tools = Path(env.get("USERPROFILE", "")) / ".dotnet" / "tools"
    env["DOTNET_ROOT"] = str(root)
    env["PATH"] = f"{root}{os.pathsep}{tools}{os.pathsep}{env.get('PATH', '')}"

    # Stryker 를 SDK 의 MSBuild 로 묶는다.
    # 이걸 안 하면 Visual Studio BuildTools 의 MSBuild(.NET Framework 판)를 골라 쓰고,
    # 그쪽은 net10.0 을 해석하지 못해 "Failed to identify target frameworks" 로 분석이 끝난다.
    # (CI(ubuntu) 에서 같은 명령이 도는 이유 — 거기엔 VS BuildTools 가 없다.)
    sdks = sorted((root / "sdk").glob("*/Sdks"), reverse=True)
    if sdks:
        env["MSBuildSDKsPath"] = str(sdks[0])
        msbuild = sdks[0].parent / "MSBuild.dll"
        if msbuild.exists():
            env["MSBUILD_EXE_PATH"] = str(msbuild)
    return env


def tool_path(name: str) -> str:
    """실행 파일의 전체 경로.

    Windows 의 `subprocess` 는 `env["PATH"]` 를 바꿔도 **부모 프로세스의 PATH** 로 실행 파일을 찾는다.
    그래서 `dotnet` 을 이름만으로 부르면 `FileNotFoundError [WinError 2]` 가 난다 (실제로 겪었다).
    """
    found = shutil.which(name)
    if found:
        return found
    local = Path(os.environ.get("LOCALAPPDATA", "")) / "Microsoft" / "dotnet" / f"{name}.exe"
    if local.exists():
        return str(local)
    tools = Path(os.environ.get("USERPROFILE", "")) / ".dotnet" / "tools" / f"{name}.exe"
    return str(tools) if tools.exists() else name


def run(cmd: list[str], timeout: int, env: dict[str, str], cwd: Path | None = None) -> tuple[int, str]:
    """외부 명령 실행.

    stdin 을 반드시 닫는다 — `claude -p` 는 파이프 입력을 3 초 기다렸다가 경고만 남기고
    빈손으로 끝난다. 이것 때문에 첫 실행에서 세 판 모두 "시험 메서드를 받지 못했다" 가 나왔다.
    """
    try:
        done = subprocess.run(cmd, cwd=cwd or ROOT, env=env, capture_output=True, text=True,
                              encoding="utf-8", errors="replace", timeout=timeout,
                              stdin=subprocess.DEVNULL)
        return done.returncode, (done.stdout or "") + (done.stderr or "")
    except subprocess.TimeoutExpired:
        return 124, f"시간 초과: {' '.join(cmd[:3])}"


def existing_test_names() -> str:
    names = []
    for path in TEST_DIR.glob("*.cs"):
        if path == GENERATED:
            continue
        names += re.findall(r"public void (\w+)\(", path.read_text(encoding="utf-8"))
    return ", ".join(sorted(names)[:60])


def format_survivors(entries: list[dict], target: str | None) -> str:
    blocks = []
    for entry in entries:
        if target and entry["file"] != target:
            continue
        muts = "\n".join(f"      - {m['mutator']}: `{m['replacement'] or '(문장 삭제)'}`"
                         for m in entry["mutations"])
        blocks.append(f"{entry['file']}:{entry['line']}\n{entry['context']}\n    바꿔도 시험이 통과한 것:\n{muts}")
    return "\n\n".join(blocks)


def ask_model(prompt: str, backend: str, model: str | None, timeout: int) -> str:
    """기본은 이 PC 에 로그인된 claude CLI(헤드리스). 키를 파일로 두지 않는다."""
    if backend == "claude":
        cmd = [tool_path("claude"), "-p", prompt]
    elif backend == "ollama":
        cmd = [tool_path("ollama"), "run", model or "qwen3.5:0.8b", prompt]
    else:
        sys.exit(f"모르는 백엔드: {backend}")
    code, out = run(cmd, timeout, dotnet_env())
    if code != 0:
        return ""
    body = re.sub(r"^```[a-zA-Z]*\n|```$", "", out.strip(), flags=re.MULTILINE)
    return body.strip()


DRAFTS = ROOT / "build" / "loop-drafts"


def keep_draft(prompt: str, stage: str, log: str | None = None) -> None:
    """생성 초안과 실패 로그를 보관한다 (`build/` 아래라 저장소에 커밋되지 않는다).

    폐기한 초안을 그냥 지웠더니 **무엇이 왜 깨졌는지 볼 수가 없었다.** 루프가 실패를 판정만 하고
    배우지 못하면 다음 판을 고칠 근거가 없다.
    """
    DRAFTS.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%H%M%S")
    if GENERATED.exists():
        shutil.copy(GENERATED, DRAFTS / f"{stamp}-{prompt}-{stage}.cs")
    if log:
        (DRAFTS / f"{stamp}-{prompt}-{stage}.log").write_text(log, encoding="utf-8")


def restore(backup: str | None) -> None:
    """생성 파일을 원래대로 되돌린다 — 예외로 끝나도 저장소를 더럽히지 않게.

    실제로 `dotnet` 을 못 찾아 예외가 났을 때 초안이 저장소에 남았다. 그 뒤로 이 정리를
    `finally` 에서 부른다.
    """
    if backup is None:
        GENERATED.unlink(missing_ok=True)
    else:
        GENERATED.write_text(backup, encoding="utf-8")


def write_generated(methods: str) -> int:
    """생성된 시험 메서드를 한 파일에 담는다. 실패하면 이 파일만 지우면 된다."""
    count = len(re.findall(r"public void \w+\(", methods))
    GENERATED.write_text(
        "// 자동 생성 — tools/mutation_loop/loop.py 가 만든 초안이다. 사람이 검토하기 전에는 커밋하지 않는다.\n"
        "namespace SpaceLink.Tests;\n\n"
        "public class MutationLoopGeneratedTests\n{\n" + methods + "\n}\n",
        encoding="utf-8")
    return count


def mutation_survivors(target: str | None, env: dict[str, str]) -> tuple[int, list[str]]:
    """대상 파일만 변이시켜 다시 잰다. 전체(20 분)가 아니라 몇 분이면 끝난다."""
    glob = f"**/{target}" if target else "**/*.cs"
    # Stryker 는 **시험 프로젝트 폴더**에서 돌려야 한다 (CI 의 working-directory 와 같게).
    # 저장소 루트에서 부르면 "No .csproj or .fsproj file found" 로 죽는다 — 실제로 겪었다.
    code, out = run([tool_path("dotnet-stryker"), "--project", "SpaceLink.csproj", "--mutate", glob,
                     "--reporter", "json", "--output", "../../build/stryker-loop"], 1800, env,
                    cwd=TEST_DIR)
    produced = sorted((ROOT / "build" / "stryker-loop").rglob("mutation-report.json"))
    if not produced:
        return -1, [f"stryker 실패 (코드 {code}): {out[-300:]}"]
    data = json.loads(produced[-1].read_text(encoding="utf-8"))
    survived = [f"{Path(f).name}:{m['location']['start']['line']}"
                for f, fr in data["files"].items() for m in fr["mutants"] if m["status"] == "Survived"]
    return len(survived), survived


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--report", type=Path, required=True, help="직전 Stryker 리포트(html)")
    parser.add_argument("--prompts", default="bare,context,guarded", help="쉼표로 구분한 프롬프트 판")
    parser.add_argument("--target", help="이 파일의 변이만 다룬다 (예: PacketExtractor.cs)")
    parser.add_argument("--backend", default="claude", choices=["claude", "ollama"])
    parser.add_argument("--model", help="ollama 모델 이름")
    parser.add_argument("--timeout", type=int, default=300, help="모델 호출 제한 시간(초)")
    parser.add_argument("--dry-run", action="store_true", help="프롬프트만 찍고 모델을 부르지 않는다")
    args = parser.parse_args()

    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    sys.path.insert(0, str(Path(__file__).parent))
    from survivors import collect, load_report  # noqa: E402

    _, entries = collect(load_report(args.report), context=6)
    survivors_text = format_survivors(entries, args.target)
    if not survivors_text:
        print("대상에 해당하는 생존 뮤턴트가 없다.")
        return 0

    env = dotnet_env()
    existing = existing_test_names()
    results: list[Outcome] = []
    backup = GENERATED.read_text(encoding="utf-8") if GENERATED.exists() else None

    try:
        run_prompts(args, survivors_text, existing, env, results, entries)
    finally:
        # 어떤 경로로 끝나도(예외 포함) 생성 파일을 되돌린다.
        restore(backup)

    if results:
        print("\n| 프롬프트 | 생성 | 컴파일 | 시험 | 생존 전→후 | 판정 | 초 |")
        print("|---|---|---|---|---|---|---|")
        for r in results:
            print(f"| {r.prompt} | {r.generated} | {'O' if r.compiled else 'X'} | "
                  f"{'O' if r.tests_passed else 'X'} | {r.survivors_before} → "
                  f"{r.survivors_after if r.survivors_after >= 0 else '?'} | {r.note} | {r.seconds:.0f} |")
    return 0


def run_prompts(args, survivors_text: str, existing: str, env: dict[str, str],
                results: list[Outcome], entries: list[dict]) -> None:
    """프롬프트 판마다 한 바퀴: 생성 → 컴파일·시험 → 뮤테이션 재측정 → 채택/폐기."""
    for name in args.prompts.split(","):
        name = name.strip()
        if name not in PROMPTS:
            print(f"모르는 프롬프트 판: {name}")
            continue
        prompt = PROMPTS[name].format(survivors=survivors_text, existing=existing, api=PUBLIC_API)
        if args.dry_run:
            print(f"\n{'=' * 70}\n[{name}] 프롬프트 {len(prompt):,} 자\n{'=' * 70}\n{prompt[:1200]}\n...")
            continue

        started = time.time()
        outcome = Outcome(prompt=name)
        print(f"\n[{name}] 모델 호출 …", flush=True)
        methods = ask_model(prompt, args.backend, args.model, args.timeout)
        if not methods or "public void" not in methods:
            outcome.note = "시험 메서드를 받지 못했다"
            outcome.seconds = time.time() - started
            results.append(outcome)
            continue

        outcome.generated = write_generated(methods)
        keep_draft(name, "generated")
        code, out = run([tool_path("dotnet"), "test", "tests/SpaceLink.Tests", "-c", "Release"], 900, env)
        outcome.compiled = "error CS" not in out
        outcome.tests_passed = code == 0
        if not outcome.tests_passed:
            # 왜 실패했는지 남기지 않으면 다음 판을 고칠 근거가 없다 — 초안과 오류를 함께 보관한다.
            errors = [line.strip() for line in out.splitlines() if "error CS" in line or "error MSB" in line]
            outcome.errors = errors[:8]
            outcome.note = "컴파일 실패" if not outcome.compiled else "시험 실패"
            keep_draft(name, "failed", "\n".join(errors) or out[-4000:])
            GENERATED.unlink(missing_ok=True)
            outcome.seconds = time.time() - started
            results.append(outcome)
            print(f"    → {outcome.note}: {errors[0][:160] if errors else '(오류 줄을 찾지 못함)'}")
            continue

        after, detail = mutation_survivors(args.target, env)
        outcome.survivors_after = after
        outcome.survivors_before = sum(len(e["mutations"]) for e in entries
                                       if not args.target or e["file"] == args.target)
        outcome.accepted = 0 <= after < outcome.survivors_before
        outcome.killed = detail[:10]
        if after < 0:
            # 측정이 안 된 것과 "줄지 않았다" 는 다르다 — 섞으면 기록이 거짓말을 한다.
            outcome.note = "뮤테이션 측정 실패 (판정 보류)"
            outcome.errors = detail[:3]
            keep_draft(name, "unmeasured", "\n".join(detail))
        else:
            outcome.note = "채택" if outcome.accepted else "생존이 줄지 않아 폐기"
        if not outcome.accepted:
            GENERATED.unlink(missing_ok=True)
        else:
            shutil.copy(GENERATED, ROOT / "build" / f"generated-{name}.cs")
        outcome.seconds = time.time() - started
        results.append(outcome)
    return 0


if __name__ == "__main__":
    sys.exit(main())
