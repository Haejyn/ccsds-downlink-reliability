#!/usr/bin/env python3
"""README 그림을 만든다 — docs/img/*.png.

- performance.png      최적화 전 단계별 시간 · 수신 체인 처리량 (v1.0 · 단일 · 병렬) — 수치는 시험 보고서 §8.6
- astrocast_signal.png Astrocast 0.1 녹음의 실제 파형 · 표본 시점 · 눈 다이어그램 (gen_golden_capture.py 가 받은 녹음)
- eirsat_decode.png    EIRSAT-1 CADU 46 장의 복호 결과 — tools/CaptureReport(이 저장소의 수신 체인)가 낸 CSV

사용: python tools/make_readme_figures.py   (matplotlib · numpy, 한글 글꼴은 맑은 고딕 → Noto Sans CJK 순으로 찾는다)
"""
import csv
import io
import subprocess
import sys
import wave
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from matplotlib import font_manager

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "docs" / "img"
sys.path.insert(0, str(ROOT / "tools"))
import gen_golden_capture as capture   # noqa: E402 — 녹음을 받는 함수를 같이 쓴다

INK, MUTED, ACCENT, GOOD, WARN, BAD = "#1f2937", "#9ca3af", "#2563eb", "#16a34a", "#f59e0b", "#d1d5db"


def setup_fonts() -> None:
    for name in ("Malgun Gothic", "Noto Sans CJK KR", "NanumGothic", "AppleGothic"):
        if any(f.name == name for f in font_manager.fontManager.ttflist):
            plt.rcParams["font.family"] = name
            break
    plt.rcParams.update({"axes.spines.top": False, "axes.spines.right": False, "axes.edgecolor": MUTED,
                         "axes.labelcolor": INK, "xtick.color": INK, "ytick.color": INK, "figure.dpi": 150,
                         "axes.unicode_minus": False})


def performance() -> None:
    # 시험 보고서 §8.6 — 왼쪽: 최적화 전 단계별 실측(Stopwatch, CADU 164 B), 오른쪽: 체인 · 오류 없음 BenchmarkDotNet --job short
    fig, (donut, ax) = plt.subplots(1, 2, figsize=(10, 3.4), gridspec_kw={"width_ratios": [1, 1.4]})

    stages = [("RS 신드롬", 16413, ACCENT), ("ASM 동기", 1277, "#60a5fa"), ("PN", 362, "#93c5fd"), ("패킷 추출", 343, "#bfdbfe")]
    total = sum(v for _, v, _ in stages)
    donut.pie([v for _, v, _ in stages], colors=[c for _, _, c in stages], startangle=90, counterclock=False,
              wedgeprops={"width": 0.36, "edgecolor": "white", "linewidth": 2})
    donut.text(0, 0.08, "89 %", ha="center", va="center", fontsize=20, fontweight="bold", color=ACCENT)
    donut.text(0, -0.2, "RS 신드롬", ha="center", va="center", fontsize=9, color=INK)
    donut.legend([f"{name}  {value / 1000:.1f} µs" for name, value, _ in stages], loc="center left", bbox_to_anchor=(0.95, 0.5),
                 frameon=False, fontsize=9)
    donut.set_title(f"최적화 전 · CADU 한 장 {total / 1000:.1f} µs", loc="left", fontsize=11, color=INK)

    labels = ["v1.0\n단일 스레드", "v1.1\n단일 스레드", "v1.1\nRS 병렬"]
    gbps = [0.072, 1.34, 1.92]
    bars = ax.bar(labels, gbps, color=[MUTED, ACCENT, ACCENT], width=0.55)
    ax.axhline(2.0, color=WARN, linestyle="--", linewidth=1.2)
    ax.text(-0.42, 2.04, "2 Gbps", color=WARN, ha="left", va="bottom", fontsize=9)
    for bar, value in zip(bars, gbps):
        centre = bar.get_x() + bar.get_width() / 2
        if value < 1:
            ax.text(centre, value + 0.05, f"{value * 1000:.0f} Mbps", ha="center", va="bottom", fontsize=10, color=INK, fontweight="bold")
        else:
            ax.text(centre, value - 0.08, f"{value:.2f} Gbps", ha="center", va="top", fontsize=10, color="white", fontweight="bold")
    ax.set_ylabel("채널 비트율 (Gbps)")
    ax.set_ylim(0, 2.4)
    ax.set_title("수신 체인 처리량 · 단일 ×18.7", loc="left", fontsize=11, color=INK)
    fig.tight_layout()
    fig.savefig(OUT / "performance.png", facecolor="white")
    plt.close(fig)


def astrocast_signal(work: Path) -> None:
    path = capture.fetch(work)
    with wave.open(str(path)) as w:
        rate = w.getframerate()
        x = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(float)
    x -= np.convolve(x, np.ones(481) / 481, mode="same")
    x /= np.percentile(np.abs(x), 99)
    bits, baud = capture.demodulate(path)
    period = rate / baud

    # 첫 ASM 이 시작하는 곳 — 복조 비트(극성 뒤집음)에서 찾는다
    marker = np.array([(capture.ASM >> (31 - i)) & 1 for i in range(32)], dtype=np.uint8)
    inverted = 1 - bits
    windows = np.lib.stride_tricks.sliding_window_view(inverted, 32)
    first = int(np.nonzero(np.count_nonzero(windows != marker, axis=1) == 0)[0][0])

    n = np.arange(len(x) - 1)
    c = n[(x[:-1] < 0) != (x[1:] < 0)]
    crossings = c + x[c] / (x[c] - x[c + 1])
    phase = np.angle(np.mean(np.exp(2j * np.pi * crossings / period))) / (2 * np.pi) * period % period
    centers = phase + period / 2 + np.arange(len(bits)) * period

    fig, (ax, eye) = plt.subplots(1, 2, figsize=(10, 3.0), gridspec_kw={"width_ratios": [3, 1]})
    start, stop = first - 12, first + 36
    t0, t1 = int(centers[start] - period / 2), int(centers[stop] + period / 2)
    ax.plot(np.arange(t0, t1) / rate * 1e3, x[t0:t1], color=INK, linewidth=1.1)
    for k in range(start, stop + 1):
        colour = ACCENT if first <= k < first + 32 else MUTED
        ax.plot(centers[k] / rate * 1e3, x[int(round(centers[k]))], "o", color=colour, markersize=3.5)
    ax.axvspan(centers[first] / rate * 1e3 - period / rate * 500, centers[first + 31] / rate * 1e3 + period / rate * 500,
               color=ACCENT, alpha=0.08)
    ax.text(centers[first + 16] / rate * 1e3, 1.25, "ASM 1ACFFC1D", color=ACCENT, ha="center", fontsize=9)
    ax.set_ylim(-1.5, 1.5)
    ax.set_xlabel("시간 (ms)")
    ax.set_yticks([])
    ax.set_title(f"Astrocast 0.1 실제 신호 · {baud:.0f} baud 실측 (공칭 9600)", loc="left", fontsize=11, color=INK)

    span = int(round(2 * period))
    for k in range(40, min(len(bits) - 2, 1500)):
        s = int(round(centers[k] - period))
        eye.plot(np.arange(span) / period, x[s:s + span], color=ACCENT, alpha=0.04, linewidth=0.8)
    eye.axvline(1.0, color=WARN, linewidth=1)
    eye.set_xticks([0, 1, 2])
    eye.set_xticklabels(["", "표본", ""])
    eye.set_yticks([])
    eye.set_ylim(-1.5, 1.5)
    eye.set_title("눈 다이어그램", loc="left", fontsize=11, color=INK)
    fig.tight_layout()
    fig.savefig(OUT / "astrocast_signal.png", facecolor="white")
    plt.close(fig)


def eirsat_decode() -> None:
    result = subprocess.run(["dotnet", "run", "-c", "Release", "--project", str(ROOT / "tools" / "CaptureReport"), "--",
                             str(ROOT / "tests" / "SpaceLink.Tests" / "golden")], check=True, capture_output=True, text=True)
    rows = list(csv.DictReader(io.StringIO(result.stdout)))
    colours = {"satnogs": GOOD, "extra": WARN, "failed": BAD}
    heights = [int(r["corrected"]) if r["status"] != "failed" else 64 for r in rows]
    fig, ax = plt.subplots(figsize=(10, 3.0))
    bars = ax.bar(range(len(rows)), heights, color=[colours[r["status"]] for r in rows], width=0.8)
    for bar, row in zip(bars, rows):
        if row["status"] == "failed":   # 정정 수가 아니라 "한계를 넘음" 이라는 표시 — 빗금으로 구분한다
            bar.set_hatch("///")
            bar.set_edgecolor("white")
    ax.axhline(64, color=MUTED, linestyle=":", linewidth=1)
    ax.text(len(rows) - 0.5, 65, "정정 한계 64 (레인 4 × 16)", color=MUTED, ha="right", va="bottom", fontsize=8)
    counts = {k: sum(r["status"] == k for r in rows) for k in colours}
    handles = [plt.Rectangle((0, 0), 1, 1, color=colours[k]) for k in colours]
    ax.legend(handles, [f"SatNOGS 와 같음 {counts['satnogs']}", f"SatNOGS 가 놓친 프레임 {counts['extra']}", f"정정 한계 넘음 {counts['failed']}"],
              loc="upper left", frameon=False, fontsize=9, ncol=3, bbox_to_anchor=(0, 1.02))
    ax.set_xlabel("CADU (시간 순)")
    ax.set_ylabel("정정한 심볼 수")
    ax.set_ylim(0, 78)
    ax.set_xlim(-1, len(rows))
    ax.set_title("EIRSAT-1 실제 패스 188 초 · CADU 46 장", loc="left", fontsize=11, color=INK, pad=22)
    fig.tight_layout()
    fig.savefig(OUT / "eirsat_decode.png", facecolor="white")
    plt.close(fig)


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    OUT.mkdir(parents=True, exist_ok=True)
    setup_fonts()
    performance()
    astrocast_signal(ROOT / "build" / "capture")
    eirsat_decode()
    for path in sorted(OUT.glob("*.png")):
        print(f"썼다: {path.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
