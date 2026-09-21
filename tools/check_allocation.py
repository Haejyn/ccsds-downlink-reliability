#!/usr/bin/env python3
"""벤치마크의 **프레임당 할당**을 예산과 대조한다 (CI `benchmark` 잡).

시간이 아니라 할당으로 거는 이유는 docs/test-plan.md §5 — 공유 러너는 실행 시간이 크게 흔들려 시간 기준은
거짓 실패를 만들지만 할당량은 러너 성능과 무관하다. 벤치마크가 `OperationsPerInvoke` 로 프레임 수를 이미 나눠
주므로 `BytesAllocatedPerOperation` 이 곧 프레임당 바이트다 — 이 스크립트에는 프레임 수 상수가 없다.
(예전에는 여기에 78,001 이 박혀 있었는데 벤치마크 입력의 실제 장수는 78,089 였다.)

실패 조건
  1. 예산을 넘은 벤치마크
  2. 예산 표에 없는 벤치마크 — 새 벤치마크는 예산을 정해야 게이트를 통과한다
  3. 예산 표에 있는데 결과에 없는 벤치마크 — --filter 나 이름 변경으로 게이트가 눈을 감는 것을 막는다

사용: python tools/check_allocation.py build/benchmark
"""
import glob
import json
import os
import sys

# 클래스.메서드 : 프레임당 할당 상한 (바이트). 상한은 첫 실측의 약 +7 % — 개선이 되돌아가는 것만 잡고 잡음에는 흔들리지 않는다.
BUDGET = {
    "ExtractorBenchmark.Reassemble": 260,                        # 실측 243 (개선 전 443, docs/test-report.md §8)
    "ExtractorBenchmark.ReassembleWithErrorControl": 260,        # 실측 242
    "ChannelChainBenchmark.DecodeChain": 1340,                   # 실측 1,247 (오류 없음 — ASM·PN·RS 복호·추출)
    "ChannelChainBenchmark.DecodeChainWithSymbolErrors": 2620,   # 실측 2,443 (부호어당 심볼 오류 8 개)
}


def load(directory: str) -> dict[str, float]:
    paths = glob.glob(os.path.join(directory, "results", "*-report-full-compressed.json"))
    if not paths:
        sys.exit(f"벤치마크 결과를 찾지 못했다: {directory}/results/*-report-full-compressed.json")
    found: dict[str, float] = {}
    for path in paths:
        with open(path, encoding="utf-8") as f:
            for case in json.load(f)["Benchmarks"]:
                found[f'{case["Type"]}.{case["Method"]}'] = case["Memory"]["BytesAllocatedPerOperation"]
    return found


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    found = load(sys.argv[1] if len(sys.argv) > 1 else "build/benchmark")

    failures: list[str] = []
    rows = []
    for name in sorted(found):
        limit = BUDGET.get(name)
        actual = found[name]
        if limit is None:
            failures.append(f"예산 표에 없는 벤치마크: {name} ({actual} B/프레임) — BUDGET 에 예산을 정해야 한다")
            rows.append((name, actual, "—", "예산 없음"))
        elif actual > limit:
            failures.append(f"할당 회귀: {name} {actual} B/프레임 > {limit}")
            rows.append((name, actual, limit, "초과"))
        else:
            rows.append((name, actual, limit, "통과"))
    for name in sorted(set(BUDGET) - set(found)):
        failures.append(f"예산 표에 있는데 결과에 없는 벤치마크: {name} — --filter 나 이름이 바뀌었나")
        rows.append((name, "—", BUDGET[name], "결과 없음"))

    for name, actual, limit, verdict in rows:
        print(f"{verdict:6s} {name}: {actual} B/프레임 (예산 {limit})")

    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write("### 프레임당 할당 예산\n\n| 벤치마크 | 실측 (B/프레임) | 예산 | 판정 |\n|---|---|---|---|\n")
            for name, actual, limit, verdict in rows:
                f.write(f"| `{name}` | {actual} | {limit} | {verdict} |\n")

    for message in failures:
        print(f"FAIL {message}", file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
