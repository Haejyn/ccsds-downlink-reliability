#!/usr/bin/env python3
"""README 그림을 만든다 — docs/img/*.png.

- astrocast_signal.png Astrocast 0.1 녹음의 실제 파형과 눈 다이어그램 (gen_golden_capture.py 가 받은 녹음)
- eirsat_decode.png    EIRSAT-1 CADU 46 장의 복호 결과 — tools/CaptureReport(이 저장소의 수신 체인)가 낸 CSV
- performance.png      최적화 전 단계별 시간 · 수신 체인 처리량 — 시험 보고서 §8.6 의 실측

글꼴은 Pretendard. 없으면 공식 릴리스(v1.3.9, OFL)를 build/fonts 로 받아 쓴다 — 저장소에는 넣지 않는다.
사용: python tools/make_readme_figures.py   (matplotlib · numpy · dotnet)
"""
import csv
import io
import os
import subprocess
import sys
import urllib.request
import wave
import zipfile
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

PRETENDARD_URL = "https://github.com/orioncactus/pretendard/releases/download/v1.3.9/Pretendard-1.3.9.zip"
BLACK, GRAY, LIGHT, BLUE, RED = "#111111", "#6b6b6b", "#d9d9d9", "#1f4e79", "#b03a2e"


def setup_style() -> None:
    fonts = ROOT / "build" / "fonts"
    candidates = [Path(os.environ["PRETENDARD_DIR"])] if "PRETENDARD_DIR" in os.environ else []
    candidates.append(fonts)
    files = [f for d in candidates if d.exists() for f in d.glob("Pretendard-*.ttf")]
    if not files:
        fonts.mkdir(parents=True, exist_ok=True)
        archive = fonts / "Pretendard.zip"
        urllib.request.urlretrieve(PRETENDARD_URL, archive)
        with zipfile.ZipFile(archive) as z:
            for name in z.namelist():
                if name.startswith("public/static/alternative/") and name.endswith(".ttf"):
                    (fonts / Path(name).name).write_bytes(z.read(name))
        files = list(fonts.glob("Pretendard-*.ttf"))
    for f in files:
        font_manager.fontManager.addfont(str(f))
    plt.rcParams.update({
        "font.family": "Pretendard", "font.size": 9, "axes.unicode_minus": False,
        "axes.linewidth": 0.8, "axes.edgecolor": BLACK, "axes.labelcolor": BLACK,
        "xtick.direction": "in", "ytick.direction": "in", "xtick.top": True, "ytick.right": True,
        "xtick.major.size": 3.5, "ytick.major.size": 3.5, "xtick.minor.size": 2, "ytick.minor.size": 2,
        "xtick.color": BLACK, "ytick.color": BLACK,
        "legend.frameon": False, "legend.fontsize": 8.5, "figure.dpi": 200, "savefig.bbox": "tight",
        "savefig.pad_inches": 0.05, "lines.linewidth": 1.0,
    })


def panel(ax, letter: str) -> None:
    ax.text(-0.02, 1.02, f"({letter})", transform=ax.transAxes, ha="right", va="bottom", fontsize=10, fontweight="semibold")


def astrocast_signal(work: Path) -> None:
    path = capture.fetch(work)
    with wave.open(str(path)) as w:
        rate = w.getframerate()
        x = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(float)
    x -= np.convolve(x, np.ones(481) / 481, mode="same")
    x /= np.percentile(np.abs(x), 99)
    bits, baud = capture.demodulate(path)
    period = rate / baud

    marker = np.array([(capture.ASM >> (31 - i)) & 1 for i in range(32)], dtype=np.uint8)
    windows = np.lib.stride_tricks.sliding_window_view(1 - bits, 32)
    first = int(np.nonzero(np.count_nonzero(windows != marker, axis=1) == 0)[0][0])
    n = np.arange(len(x) - 1)
    c = n[(x[:-1] < 0) != (x[1:] < 0)]
    crossings = c + x[c] / (x[c] - x[c + 1])
    phase = np.angle(np.mean(np.exp(2j * np.pi * crossings / period))) / (2 * np.pi) * period % period
    centers = phase + period / 2 + np.arange(len(bits)) * period

    fig, (ax, eye) = plt.subplots(1, 2, figsize=(7.2, 2.3), gridspec_kw={"width_ratios": [3, 1], "wspace": 0.18})
    start, stop = first - 10, first + 36
    t0, t1 = int(centers[start] - period / 2), int(centers[stop] + period / 2)
    ms = lambda s: s / rate * 1e3   # noqa: E731
    ax.axvspan(ms(centers[first] - period / 2), ms(centers[first + 31] + period / 2), color=LIGHT, alpha=0.6, lw=0)
    ax.plot(ms(np.arange(t0, t1)), x[t0:t1], color=BLACK, lw=0.9)
    k = np.arange(start, stop + 1)
    ax.plot(ms(centers[k]), x[np.round(centers[k]).astype(int)], "o", ms=2.6, mfc="white", mec=BLUE, mew=0.8)
    ax.text(ms(centers[first + 16]), 1.32, "ASM (0x1ACFFC1D)", ha="center", va="bottom", fontsize=8, color=GRAY)
    ax.set_xlim(ms(t0), ms(t1))
    ax.set_ylim(-1.5, 1.6)
    ax.set_xlabel("시간 (ms)")
    ax.set_ylabel("정규화 진폭")
    panel(ax, "a")

    span = int(round(2 * period))
    for i in range(40, min(len(bits) - 2, 1500)):
        s = int(round(centers[i] - period))
        eye.plot(np.arange(span) / period, x[s:s + span], color=BLACK, alpha=0.035, lw=0.6)
    eye.axvline(1.0, color=BLUE, lw=0.8, ls="--")
    eye.set_xlim(0, 2)
    eye.set_ylim(-1.5, 1.6)
    eye.set_xlabel("심볼 주기")
    eye.set_yticklabels([])
    panel(eye, "b")
    fig.savefig(OUT / "astrocast_signal.png", facecolor="white")
    plt.close(fig)


def eirsat_decode() -> None:
    result = subprocess.run(["dotnet", "run", "-c", "Release", "--project", str(ROOT / "tools" / "CaptureReport"), "--",
                             str(ROOT / "tests" / "SpaceLink.Tests" / "golden")], check=True, capture_output=True, text=True)
    rows = list(csv.DictReader(io.StringIO(result.stdout)))
    fig, ax = plt.subplots(figsize=(7.2, 2.4))
    ax.axhline(64, color=GRAY, lw=0.7, ls="--")
    ax.text(45.6, 62.5, "정정 한계 64 = 4 레인 × 16", ha="right", va="top", fontsize=7.5, color=GRAY)
    groups = [("satnogs", "SatNOGS 복호와 동일", dict(marker="o", mfc=BLACK, mec=BLACK)),
              ("extra", "SatNOGS 미복호 · 추가 복원", dict(marker="s", mfc="white", mec=BLUE, mew=1.1)),
              ("failed", "정정 불가", dict(marker="x", mec=RED, mew=1.0))]
    for status, label, style in groups:
        points = [(int(r["index"]), int(r["corrected"]) if status != "failed" else 70) for r in rows if r["status"] == status]
        ax.plot([p[0] for p in points], [p[1] for p in points], ls="none", ms=4.2, label=f"{label} ({len(points)})", **style)
        if status != "failed":
            ax.vlines([p[0] for p in points], 0, [p[1] for p in points], color=LIGHT, lw=0.8, zorder=0)
    ax.set_xlim(-1, len(rows))
    ax.set_ylim(0, 76)
    ax.set_yticks([0, 16, 32, 48, 64])
    ax.set_xlabel("CADU 순번 (시간 순)")
    ax.set_ylabel("정정 심볼 수")
    ax.legend(loc="upper left", ncol=3, bbox_to_anchor=(0, 1.16), handletextpad=0.3, columnspacing=1.2)
    fig.savefig(OUT / "eirsat_decode.png", facecolor="white")
    plt.close(fig)


def performance() -> None:
    # 시험 보고서 §8.6 — (a) 최적화 전 단계별 실측(Stopwatch, CADU 164 B), (b) 체인 · 오류 없음 BenchmarkDotNet --job short
    fig, (stages_ax, ax) = plt.subplots(1, 2, figsize=(7.2, 2.4), gridspec_kw={"width_ratios": [1.15, 1], "wspace": 0.45})
    stages = [("패킷 추출", 0.343), ("PN 되돌림", 0.362), ("ASM 동기", 1.277), ("RS 신드롬", 16.413)]
    y = np.arange(len(stages))
    stages_ax.barh(y, [v for _, v in stages], color=[GRAY, GRAY, GRAY, BLUE], height=0.55)
    for yi, (_, v) in zip(y, stages):
        stages_ax.text(v * 1.15, yi, f"{v:.2f}", va="center", fontsize=8)
    stages_ax.set_xscale("log")
    stages_ax.set_xlim(0.1, 80)
    stages_ax.set_yticks(y)
    stages_ax.set_yticklabels([s for s, _ in stages])
    stages_ax.set_xlabel("CADU 1 장 처리 시간 (µs)")
    panel(stages_ax, "a")

    labels = ["v1.0", "v1.1", "v1.1\n(병렬)"]
    gbps = [0.072, 1.34, 1.92]
    ax.bar(labels, gbps, color=[LIGHT, BLUE, BLUE], edgecolor=BLACK, linewidth=0.6, width=0.55, hatch=["", "", "///"])
    ax.axhline(2.0, color=RED, lw=0.8, ls="--")
    ax.text(-0.45, 2.04, "2 Gbps", color=RED, fontsize=7.5, va="bottom")
    for i, v in enumerate(gbps):
        ax.text(i, max(v + 0.05, 2.06) if v > 1.8 else v + 0.05, f"{v:.2f}" if v >= 1 else f"{v * 1000:.0f} Mbps", ha="center", va="bottom", fontsize=8)
    ax.set_ylim(0, 2.45)
    ax.set_ylabel("채널 비트율 (Gbps)")
    panel(ax, "b")
    fig.savefig(OUT / "performance.png", facecolor="white")
    plt.close(fig)


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    OUT.mkdir(parents=True, exist_ok=True)
    setup_style()
    astrocast_signal(ROOT / "build" / "capture")
    eirsat_decode()
    performance()
    for path in sorted(OUT.glob("*.png")):
        print(f"썼다: {path.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
