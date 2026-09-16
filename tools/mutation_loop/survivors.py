#!/usr/bin/env python3
"""
Stryker.NET 리포트에서 **생존 뮤턴트만** 뽑아 모델에 넣을 수 있는 크기로 줄인다.

  python tools/mutation_loop/survivors.py build/stryker/reports/mutation-report.html
  python tools/mutation_loop/survivors.py <report.html> --json build/stryker/survivors.json

왜 따로 두나 — 리포트 HTML 은 700 KB 가 넘는다 (변이 300 여 개 + 원본 소스 전체가 JSON 으로 박혀 있다).
그대로는 모델 창에 넣을 수 없고, 넣어도 대부분이 잡음이다. 여기서 하는 일은 셋이다.

  1. 생존(Survived) 만 남긴다 — 죽은 변이·타임아웃·컴파일 실패는 할 일이 없다.
  2. 변이 지점의 **주변 코드만** 잘라 붙인다 (기본 앞뒤 6 줄). 파일 전체는 넣지 않는다.
  3. 같은 줄에 여러 변이가 있으면 묶는다 — 한 번의 시험으로 같이 죽는 경우가 많다.

출력은 사람이 읽는 표(기본)와 기계가 읽는 JSON(--json) 둘 다 만든다.
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path


def load_report(html_path: Path) -> dict:
    """리포트 HTML 안에 박힌 mutation-testing-elements JSON 을 꺼낸다.

    Stryker 는 JSON 을 `<script>` 안에 그대로 넣는데, 키 순서·앞뒤 문자열이 판마다 다르다.
    그래서 `"schemaVersion"` 을 찾은 뒤 **뒤로 훑으며** 실제로 파싱되는 여는 중괄호를 찾는다.
    """
    html = html_path.read_text(encoding="utf-8")
    anchor = html.find('"schemaVersion"')
    if anchor < 0:
        sys.exit(f"리포트에서 JSON 을 찾지 못했다: {html_path}")
    decoder = json.JSONDecoder()
    for start in range(anchor, -1, -1):
        if html[start] != "{":
            continue
        try:
            obj, _ = decoder.raw_decode(html, start)
        except ValueError:
            continue
        if isinstance(obj, dict) and "files" in obj:
            return obj
    sys.exit(f"JSON 시작 위치를 찾지 못했다: {html_path}")


def collect(report: dict, context: int) -> tuple[dict[str, int], list[dict]]:
    """상태별 개수와, 생존 변이를 줄 단위로 묶은 목록."""
    totals: dict[str, int] = defaultdict(int)
    grouped: dict[tuple[str, int], dict] = {}

    for path, file_report in report["files"].items():
        name = path.replace("\\", "/").split("/")[-1]
        lines = file_report["source"].splitlines()
        for mutant in file_report["mutants"]:
            totals[mutant["status"]] += 1
            if mutant["status"] != "Survived":
                continue
            line_no = mutant["location"]["start"]["line"]
            key = (name, line_no)
            entry = grouped.setdefault(key, {
                "file": name,
                "line": line_no,
                "code": lines[line_no - 1].strip() if line_no - 1 < len(lines) else "",
                "context": "\n".join(
                    f"{n + 1:>5} | {lines[n]}"
                    for n in range(max(0, line_no - 1 - context), min(len(lines), line_no + context))
                ),
                "mutations": [],
            })
            entry["mutations"].append({
                "mutator": mutant["mutatorName"],
                "replacement": (mutant.get("replacement") or "").strip(),
            })

    survivors = sorted(grouped.values(), key=lambda e: (e["file"], e["line"]))
    return dict(totals), survivors


def score(totals: dict[str, int]) -> float:
    killed = totals.get("Killed", 0)
    timeout = totals.get("Timeout", 0)
    survived = totals.get("Survived", 0)
    no_coverage = totals.get("NoCoverage", 0)
    scored = killed + timeout + survived + no_coverage
    return (killed + timeout) / scored * 100 if scored else 0.0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("report", type=Path, help="Stryker 의 mutation-report.html")
    parser.add_argument("--context", type=int, default=6, help="변이 줄 앞뒤로 붙일 줄 수 (기본 6)")
    parser.add_argument("--json", type=Path, help="기계가 읽을 JSON 을 여기에 쓴다")
    args = parser.parse_args()

    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    report = load_report(args.report)
    totals, survivors = collect(report, args.context)

    print(f"점수 {score(totals):.2f} %  " + " · ".join(f"{k} {v}" for k, v in sorted(totals.items())))
    print(f"생존 {len(survivors)} 자리 (변이 {sum(len(s['mutations']) for s in survivors)} 개)\n")
    for entry in survivors:
        muts = ", ".join(f"{m['mutator']} → {m['replacement'] or '(삭제)'}" for m in entry["mutations"])
        print(f"{entry['file']}:{entry['line']}  {entry['code'][:90]}")
        print(f"    {muts[:160]}")

    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(
            json.dumps({"totals": totals, "score": score(totals), "survivors": survivors},
                       ensure_ascii=False, indent=2),
            encoding="utf-8")
        print(f"\n→ {args.json}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
