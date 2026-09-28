#!/usr/bin/env python3
"""README 애니메이션을 만든다 — docs/media/downlink.gif · downlink.mp4.

Astrocast 0.1 실제 녹음이 수신 체인을 지나가는 모습. 숫자와 바이트는 전부 실제 자료에서 온다 — 지어낸 값은 없다.

입력
- 파형: build/capture/astrocast_9k6.wav — tools/gen_golden_capture.py 의 fetch() 가 satellite-recordings 고정 커밋에서 받고
  sha256 을 확인한다 (DK3WN 녹음, Unlicense). 저장소에는 넣지 않는다.
- 표본 시점 · 경판정 비트: gen_golden_capture.demodulate() 그대로. 여기서는 표본 시각만 같은 식으로 다시 구해
  비트가 demodulate() 와 같은지, 그 비트가 tests/SpaceLink.Tests/golden/astrocast_9k6_bits.bin 과 같은지 확인한다.
- ASM 위치 · 전송 프레임 바이트 · RS · CRC: tools/DownlinkTrace (이 저장소의 C# 수신 체인, RealCaptureTests 와 같은 설정)가 낸 CSV.
  파이썬에서 복호를 다시 구현하지 않는다.

시간 축: 녹음은 3.6 초, 9677 baud. ASM 이 지나가는 구간만 느리게(약 1/130 배속), 그 사이는 빨리 넘긴다.

사용 (저장소 루트에서):
  C:\\Python314\\python.exe -m venv scripts/.venv-media
  scripts/.venv-media/Scripts/python -m pip install imageio imageio-ffmpeg numpy matplotlib scipy
  scripts/.venv-media/Scripts/python scripts/make_downlink_animation.py
dotnet(.NET 10 SDK)이 PATH 나 기본 설치 경로에 있어야 한다. 중간 프레임은 build/animation/ 에 남는다.
"""
import csv
import io
import os
import shutil
import subprocess
import sys
import wave
from pathlib import Path

import imageio.v3 as iio
import imageio_ffmpeg
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from matplotlib.patches import Rectangle

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools"))
import gen_golden_capture as capture       # noqa: E402 — 녹음 받기 · 복조
import make_readme_figures as figures      # noqa: E402 — README 그림과 같은 스타일

BLACK, GRAY, LIGHT, BLUE, RED = figures.BLACK, figures.GRAY, figures.LIGHT, figures.BLUE, figures.RED
GOLDEN = ROOT / "tests" / "SpaceLink.Tests" / "golden"
WORK = ROOT / "build" / "animation"
OUT = ROOT / "docs" / "media"
FPS = 15
SIZE, DPI = (9.6, 5.44), 125        # 1200 × 680
GIF_WIDTH = 960
WINDOW = 48                         # (a) 에 보이는 심볼 수
SLOW, FAST, HOLD = 45, 30, 36       # 프레임 수 — ASM 부근 · 사이 넘기기 · 끝 멈춤
MONO = ["DejaVu Sans Mono", "Pretendard"]    # 한글은 Pretendard 로 넘긴다


def dotnet() -> str:
    found = shutil.which("dotnet")
    if found:
        return found
    default = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "dotnet" / "dotnet.exe"
    if default.exists():
        return str(default)
    raise SystemExit("dotnet 을 찾지 못했다")


def decoder_trace() -> list[dict]:
    result = subprocess.run([dotnet(), "run", "-c", "Release", "--project", str(ROOT / "tools" / "DownlinkTrace"), "--", str(GOLDEN)],
                            check=True, capture_output=True, text=True, encoding="utf-8")
    rows = list(csv.DictReader(io.StringIO(result.stdout)))
    if not rows:
        raise SystemExit("DownlinkTrace 가 CADU 를 하나도 내지 않았다")
    return rows


def load_signal():
    path = capture.fetch(ROOT / "build" / "capture")
    with wave.open(str(path)) as w:
        rate = w.getframerate()
        x = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(float)
    x -= np.convolve(x, np.ones(481) / 481, mode="same")
    bits, baud = capture.demodulate(path)

    # 표본 시각 — demodulate() 와 같은 식. 아래 두 확인이 어긋나면 멈춘다.
    n = np.arange(len(x) - 1)
    c = n[(x[:-1] < 0) != (x[1:] < 0)]
    crossings = c + x[c] / (x[c] - x[c + 1])
    nominal = rate / capture.NOMINAL_BAUD
    periods = np.linspace(nominal * 0.98, nominal * 1.02, 4001)
    coherence = np.array([np.mean(np.exp(2j * np.pi * crossings / p)) for p in periods])
    best = int(np.argmax(np.abs(coherence)))
    period = periods[best]
    boundary = (np.angle(coherence[best]) / (2 * np.pi)) * period % period
    centers = np.arange(boundary + period / 2, len(x) - 1, period)
    if not np.array_equal((np.interp(centers, np.arange(len(x)), x) > 0).astype(np.uint8), bits):
        raise SystemExit("표본 시각이 demodulate() 와 다르다")

    golden = (GOLDEN / "astrocast_9k6_bits.bin").read_bytes()
    usable = len(bits) // 8 * 8
    decided = next((b for b in (bits, 1 - bits) if np.packbits(b[:usable]).tobytes() == golden), None)
    if decided is None:
        raise SystemExit("복조 비트가 astrocast_9k6_bits.bin 과 다르다")

    x /= np.percentile(np.abs(x), 99)
    return x, rate, baud, period, centers, decided


def timeline(rows: list[dict], total_bits: int) -> np.ndarray:
    """프레임마다 (a) 창의 가운데 심볼 번호. ASM 앞뒤 ±110 심볼은 천천히, 그 사이는 부드럽게 빨리."""
    ease = lambda u: u * u * (3 - 2 * u)    # noqa: E731
    points: list[float] = []
    asms = [int(r["asm_bit"]) for r in rows]
    for i, a in enumerate(asms):
        points += list(np.linspace(a - 110, a + 110, SLOW, endpoint=False))
        target = asms[i + 1] - 110 if i + 1 < len(asms) else total_bits - WINDOW
        u = ease(np.linspace(0, 1, FAST, endpoint=False))
        points += list(a + 110 + (target - a - 110) * u)
    points += [total_bits - WINDOW] * HOLD
    return np.clip(np.array(points), WINDOW, total_bits - WINDOW)


def hex_bytes(h: str) -> str:
    pairs = [h[i:i + 2] for i in range(0, len(h), 2)]
    return " ".join(pairs[:8]) + "  " + " ".join(pairs[8:])


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    rows = decoder_trace()
    x, rate, baud, period, centers, decided = load_signal()
    figures.setup_style()
    plt.rcParams.update({"figure.dpi": DPI, "savefig.bbox": None})
    cadu_bits = (4 + 1275) * 8
    ms = lambda s: s / rate * 1e3          # noqa: E731
    sec = lambda s: s / rate               # noqa: E731
    play = timeline(rows, len(decided))

    fig = plt.figure(figsize=SIZE, facecolor="white")

    # 전체 녹음 — 지금 어디를 보는지 · CADU 가 어디까지 들어왔는지
    over = fig.add_axes((0.075, 0.855, 0.9, 0.085))
    bins = np.array_split(x, 900)
    t_bins = np.linspace(0, sec(len(x)), len(bins))
    over.fill_between(t_bins, [b.min() for b in bins], [b.max() for b in bins], color=LIGHT, lw=0)
    over.set_xlim(0, sec(len(x)))
    over.set_ylim(-1.8, 1.8)
    over.set_yticks([])
    over.set_xlabel("녹음 시간 (s)", labelpad=1)
    over.tick_params(axis="x", pad=2)
    spans, span_labels = [], []
    for r in rows:
        a = int(r["asm_bit"])
        spans.append(over.add_patch(Rectangle((sec(centers[a] - period / 2), -1.8), 0, 3.6, color=BLUE, alpha=0.16, lw=0)))
        span_labels.append(over.text(sec(centers[a]) + 0.015, 1.35, f"CADU {int(r['index']) + 1}", fontsize=7.5, color=BLUE,
                                     va="top", visible=False))
    head = over.axvline(0, color=BLACK, lw=1.0)
    clock = over.text(1.0, 1.08, "", transform=over.transAxes, ha="right", va="bottom", fontsize=8, color=GRAY)
    over.text(0.0, 1.08, "Astrocast 0.1 · 437.150 MHz 9k6 FSK · DK3WN 녹음", transform=over.transAxes, ha="left", va="bottom",
              fontsize=8, color=GRAY)

    # (a) 파형 · 표본 시점 · 경판정 비트
    ax = fig.add_axes((0.075, 0.43, 0.6, 0.31))
    figures.panel(ax, "a")
    asm_box = ax.add_patch(Rectangle((0, -1.8), 0, 3.6, color=LIGHT, alpha=0.6, lw=0))
    asm_text = ax.text(0, 1.33, "ASM 0x1ACFFC1D", ha="center", va="bottom", fontsize=8, color=GRAY, family=MONO)
    (wave_line,) = ax.plot([], [], color=BLACK, lw=0.9)
    (dots,) = ax.plot([], [], "o", ms=3.0, mfc="white", mec=BLUE, mew=0.8)
    bit_texts = [ax.text(0, -1.5, "", ha="center", va="center", fontsize=6.5, family=MONO, color=GRAY) for _ in range(WINDOW + 2)]
    ax.set_ylim(-1.75, 1.7)
    ax.set_yticks([-1, 0, 1])
    ax.set_xlabel("시간 (ms)")
    ax.set_ylabel("정규화 진폭")

    # (b) 눈 다이어그램 — 지나간 심볼을 계속 쌓는다
    eye = fig.add_axes((0.735, 0.43, 0.24, 0.31))
    figures.panel(eye, "b")
    eye_y = (-1.75, 1.7)
    grid = np.zeros((140, 160))
    eye_img = eye.imshow(grid, extent=(0, 2, *eye_y), origin="lower", aspect="auto", cmap="Greys", vmin=0, vmax=1,
                         interpolation="bilinear")
    eye.axvline(1.0, color=BLUE, lw=0.8, ls="--")
    eye.set_xlim(0, 2)
    eye.set_ylim(*eye_y)
    eye.set_yticklabels([])
    eye.set_xlabel("심볼 주기")
    eye_count = eye.text(0.97, 0.04, "", transform=eye.transAxes, ha="right", va="bottom", fontsize=7.5, color=GRAY,
                         bbox={"facecolor": "white", "edgecolor": "none", "pad": 1.0, "alpha": 0.85})
    u = (np.arange(4 * grid.shape[1]) + 0.5) / (2 * grid.shape[1])   # 열마다 네 점 — 선이 끊기지 않게
    samples = np.arange(len(x))

    # (c) 복호 기록 — tools/DownlinkTrace 출력 그대로
    log = fig.add_axes((0.075, 0.045, 0.9, 0.255))
    figures.panel(log, "c")
    log.set_xticks([])
    log.set_yticks([])
    log.set_xlim(0, 1)
    log.set_ylim(0, 1)
    columns = [0.012, 0.045, 0.125, 0.215, 0.62, 0.79, 0.88]
    headers = ["#", "시각", "ASM", "전송 프레임 첫 16 바이트", "헤더", "RS", "CRC-16"]
    for cx, h in zip(columns, headers):
        log.text(cx, 0.86, h, fontsize=8, color=GRAY, va="center")
    line_y = [0.66, 0.48, 0.30]
    mono = {"family": MONO}
    cells = [[log.text(cx, y, "", fontsize=8, va="center", color=BLACK, **(mono if k in (0, 1, 3, 6) else {}))
              for k, cx in enumerate(columns)] for y in line_y]
    summary = log.text(0.012, 0.1, "", fontsize=8, va="center", color=BLUE)

    frames_dir = WORK / "frames"
    if frames_dir.exists():
        shutil.rmtree(frames_dir)
    frames_dir.mkdir(parents=True)

    stacked = 2          # 눈 다이어그램에 넣은 마지막 심볼 + 1
    for f, center in enumerate(play):
        k0 = int(round(center - WINDOW / 2))
        k1 = k0 + WINDOW
        s0, s1 = int(centers[k0] - period / 2), int(centers[k1] + period / 2)
        now_bit = k1                                     # 창의 오른쪽 끝까지 받았다
        wave_line.set_data(ms(np.arange(s0, s1)), x[s0:s1])
        ks = np.arange(k0, k1 + 1)
        dots.set_data(ms(centers[ks]), np.interp(centers[ks], samples, x))
        ax.set_xlim(ms(s0), ms(s1))

        visible_asm = None
        for r in rows:
            a = int(r["asm_bit"])
            if a + 32 > k0 and a < k1 + 1:
                visible_asm = a
        for text, k in zip(bit_texts, range(k0, k0 + len(bit_texts))):
            if k <= k1:
                in_asm = visible_asm is not None and visible_asm <= k < visible_asm + 32
                text.set_position((ms(centers[k]), -1.5))
                text.set_text(str(decided[k]))
                text.set_color(BLUE if in_asm else GRAY)
            else:
                text.set_text("")
        if visible_asm is not None:
            left, right = ms(centers[visible_asm] - period / 2), ms(centers[visible_asm + 31] + period / 2)
            asm_box.set_bounds(left, -1.8, right - left, 3.6)
            mid = min(max((left + right) / 2, ms(s0) + 1.6), ms(s1) - 1.6)
            asm_text.set_position((mid, 1.33))
            asm_box.set_visible(True)
            asm_text.set_visible(True)
        else:
            asm_box.set_visible(False)
            asm_text.set_visible(False)

        # 눈 다이어그램 누적
        upto = min(now_bit, len(centers) - 3)
        if upto > stacked:
            ks = np.arange(stacked, upto)
            t = centers[ks][:, None] - period + u[None, :] * period
            y = np.interp(t, samples, x)
            h, _, _ = np.histogram2d(y.ravel(), np.broadcast_to(u, y.shape).ravel(), bins=grid.shape,
                                     range=(eye_y, (0, 2)))
            grid += h
            stacked = upto
        eye_img.set_data(np.sqrt(grid / grid.max()) if grid.max() else grid)
        eye_count.set_text(f"누적 {stacked - 2:,} 심볼")

        # 전체 녹음 · 복호 기록
        t_now = sec(centers[now_bit])
        head.set_xdata([t_now, t_now])
        clock.set_text(f"t = {t_now:.3f} s / {sec(len(x)):.2f} s")
        done = 0
        for r, span, label, row in zip(rows, spans, span_labels, cells):
            a = int(r["asm_bit"])
            emitted_bit = (int(r["emitted_after_byte"]) + 1) * 8
            found = now_bit >= a + 32
            complete = now_bit >= emitted_bit
            span.set_visible(found)
            label.set_visible(found)
            if found:
                right = centers[min(now_bit, a + cadu_bits - 1)] + period / 2
                span.set_x(sec(centers[a] - period / 2))
                span.set_width(sec(right - centers[a] + period / 2))
            if not found:
                for cell in row:
                    cell.set_text("")
                continue
            row[0].set_text(str(int(r["index"]) + 1))
            row[1].set_text(f"{sec(centers[a]):.3f} s")
            row[2].set_text(f"오류 {r['asm_bit_errors']} 비트")
            if not complete:
                got = min(now_bit - a, cadu_bits) // 8
                row[3].set_text(f"수신 중 {got:>4} / {cadu_bits // 8} B")
                row[3].set_color(GRAY)
                for cell in row[4:]:
                    cell.set_text("")
                continue
            done += 1
            row[3].set_color(BLACK)
            if r["rs_ok"] == "True":
                row[3].set_text(hex_bytes(r["head_hex"]))
                row[5].set_text(f"정정 {r['corrected']}")
                row[5].set_color(BLACK)
            else:
                row[3].set_text("—")
                row[5].set_text("복호 실패")
                row[5].set_color(RED)
            if r["frame_check"] == "None":
                row[4].set_text(f"SCID {r['scid']} · VC {r['vcid']} · MC {r['mc_count']}")
                row[6].set_text(f"{r['crc']} 일치")
                row[6].set_color(BLACK)
            else:
                row[4].set_text("")
                row[6].set_text(r["frame_check"] or "—")
                row[6].set_color(RED)
        if done == len(rows):
            ok = sum(r["rs_ok"] == "True" and r["frame_check"] == "None" for r in rows)
            corrected = sum(int(r["corrected"]) for r in rows)
            counts = [r["mc_count"] for r in rows]
            summary.set_text(f"CADU {ok}/{len(rows)} 복호 · RS 정정 심볼 {corrected} · CRC {ok}/{len(rows)} 일치 · "
                             f"마스터 프레임 카운트 {counts[0]} → {counts[-1]} 연속  ({baud:.0f} baud 실측)")
        else:
            summary.set_text("")

        fig.canvas.draw()
        iio.imwrite(frames_dir / f"{f:04d}.png", np.asarray(fig.canvas.buffer_rgba())[..., :3])
    plt.close(fig)

    OUT.mkdir(parents=True, exist_ok=True)
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    pattern = str(frames_dir / "%04d.png")
    run = lambda *a: subprocess.run([ffmpeg, "-y", "-loglevel", "error", *a], check=True)   # noqa: E731
    run("-framerate", str(FPS), "-i", pattern, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "20", "-preset", "slow",
        "-movflags", "+faststart", str(OUT / "downlink.mp4"))
    palette = WORK / "palette.png"
    scale = f"scale={GIF_WIDTH}:-1:flags=lanczos"
    run("-framerate", str(FPS), "-i", pattern, "-vf", f"{scale},palettegen=max_colors=64:stats_mode=full", str(palette))
    run("-framerate", str(FPS), "-i", pattern, "-i", str(palette), "-lavfi",
        f"{scale}[v];[v][1:v]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle", "-loop", "0", str(OUT / "downlink.gif"))

    for path in (OUT / "downlink.gif", OUT / "downlink.mp4"):
        print(f"썼다: {path.relative_to(ROOT)} ({path.stat().st_size / 1e6:.2f} MB)")
    print(f"프레임 {len(play)} 장 · {len(play) / FPS:.1f} 초 · {FPS} fps")
    return 0


if __name__ == "__main__":
    sys.exit(main())
