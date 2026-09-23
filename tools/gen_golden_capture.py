#!/usr/bin/env python3
"""실제 위성 녹음을 복조해 수신 체인의 시험 입력(비트열)을 만든다 — 이 저장소가 **실제 링크의 비트**를 복호하는지 보려고.

libfec 대조(§9.10)는 "널리 쓰이는 공개 구현과 같다" 였다. 이 입력은 궤도에 있는 위성이 실제로 보낸 전파를 녹음한 것이라,
ASM · PN · RS(이중 기저·인터리빙) · 프레임 CRC 의 비트 배치가 **실제 송신기와** 같은지를 한 번에 확인한다.

- 녹음: Astrocast 0.1 (NORAD 43798) 437.150 MHz 9k6 FSK, DK3WN 기여 — gr-satellites 의 satellite-recordings 저장소
  (Unlicense, 퍼블릭 도메인). 고정 커밋에서 받아 sha256 을 확인한다. 녹음 자체는 저장소에 넣지 않고 복조한 비트만 넣는다.
- 프레이밍(gr-satellites `Astrocast_0_1.yml`): CCSDS Reed-Solomon, 프레임 1115 B, 이중 기저, 인터리빙 5, PN 255 비트.
- 복조: FM 복조 뒤 오디오(48 kHz)라 기저대역 NRZ 다. 영점 교차 시각을 선형 보간으로 구하고, 교차가 한 격자 위에 가장 잘 모이는
  심볼 주기를 찾아(원형 평균의 크기) 비트 가운데에서 경판정한다. 녹음이 짧고(3.6 초) 신호가 깨끗해 고정 주기로 충분하다.
  ⚠ 공칭 9600 baud 로 두면 전부 틀린다 — 이 녹음의 실측은 약 9677 baud(0.8 % 빠름)이고, 3 장 × 5 레인이 전부 RS 실패로 나왔다.
- 극성: FSK 의 mark/space 대응은 수신기마다 달라, ASM 이 보이는 쪽을 고른다.

두 번째 녹음 — EIRSAT-1 (NORAD 58472) 437.100 MHz 9k6 GMSK, SatNOGS 관측 12324560 (EA5WA 지상국, 2025-09-04, 188 초, CC BY-SA 4.0).
프레이밍(gr-satellites `EIRSAT-1.yml`): 프레임 892 B(= 223·4), 인터리빙 4, 이중 기저, PN 255 비트. FECF·OCF 없음.
- 잡음이 섞인 실제 링크라 RS 가 실제로 정정한다. 같은 녹음을 SatNOGS 지상국이 자체 복호한 프레임 26 장도 받아 **기준**으로 넣는다.
- 신호가 버스트로 들어오고 구간마다 시계가 달라, 3 초 창을 0.5 초씩 밀며 창마다 심볼 주기를 따로 잰다. 창에서 ASM(비트 오류 ≤ 3)을
  찾으면 그 뒤 코드블록까지 **경판정 비트 그대로**(되돌림·정정 없이) ASM 과 함께 잘라 시간 순으로 잇는다 — 수신 체인은 이것을 스트림으로 받는다.
- ⚠ 이 파일은 복조기가 찾은 CADU 만 이은 것이다 — 버스트 사이의 잡음 구간은 넣지 않았다(188 초 전체를 넣으면 225 KB).

사용:
  python tools/gen_golden_capture.py            # 녹음을 받아 복조하고 저장소에 쓴다
  python tools/gen_golden_capture.py --check    # 다시 만들어 저장소 파일과 대조한다(CI). 다르면 종료 코드 1
numpy 가 있어야 한다. EIRSAT-1 녹음(ogg)은 soundfile 로 읽는다.
"""
import argparse
import hashlib
import sys
import urllib.request
import wave
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parent.parent
GOLDEN = ROOT / "tests" / "SpaceLink.Tests" / "golden" / "astrocast_9k6_bits.bin"
RECORDING_COMMIT = "ea2bd26bd8dd5824ed910a0d101b65a9f734231f"
RECORDING_URL = f"https://raw.githubusercontent.com/daniestevez/satellite-recordings/{RECORDING_COMMIT}/astrocast_9k6.wav"
RECORDING_SHA256 = "078f638ea4b10466952fdd909c7464d34be0871dc492fc81af84bc5a4efb0206"
NOMINAL_BAUD = 9600
ASM = 0x1ACFFC1D

EIRSAT_CADUS = ROOT / "tests" / "SpaceLink.Tests" / "golden" / "eirsat1_cadus.bin"
EIRSAT_SATNOGS = ROOT / "tests" / "SpaceLink.Tests" / "golden" / "eirsat1_satnogs_frames.bin"
EIRSAT_OBSERVATION = 12324560
EIRSAT_URL = ("https://archive.org/download/satnogs-observations-012320001-012330000/"
              "satnogs-observations-012324001-012325000.zip/satnogs_12324560_2025-09-04T12-26-31.ogg")
EIRSAT_SHA256 = "eaf8bd1d339622f11ea7cbfe734048ce976babc881cc27eff3081be250f4ce23"
EIRSAT_CODEBLOCK = 1020   # 255 · 4
EIRSAT_FRAME = 892


def fetch(work: Path) -> Path:
    path = work / "astrocast_9k6.wav"
    if not path.exists():
        work.mkdir(parents=True, exist_ok=True)
        urllib.request.urlretrieve(RECORDING_URL, path)
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if digest != RECORDING_SHA256:
        raise SystemExit(f"녹음의 sha256 이 다르다: {digest}")
    return path


def demodulate(path: Path) -> tuple[np.ndarray, float]:
    with wave.open(str(path)) as w:
        rate = w.getframerate()
        x = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(float)

    # 느린 DC 흔들림 제거 — 약 100 비트 길이의 이동 평균
    x -= np.convolve(x, np.ones(481) / 481, mode="same")

    # 영점 교차 시각(선형 보간). NRZ 의 교차는 비트 경계에만 오므로 한 주기 격자 위에 모인다.
    n = np.arange(len(x) - 1)
    c = n[(x[:-1] < 0) != (x[1:] < 0)]
    crossings = c + x[c] / (x[c] - x[c + 1])

    # 공칭 주기의 ±2 % 안에서 교차가 가장 잘 모이는 주기를 찾는다 — 1/10,000 샘플 간격.
    nominal = rate / NOMINAL_BAUD
    periods = np.linspace(nominal * 0.98, nominal * 1.02, 4001)
    coherence = np.array([np.mean(np.exp(2j * np.pi * crossings / p)) for p in periods])
    best = int(np.argmax(np.abs(coherence)))
    period = periods[best]
    boundary = (np.angle(coherence[best]) / (2 * np.pi)) * period % period

    t = np.arange(boundary + period / 2, len(x) - 1, period)   # 비트 가운데
    bits = (np.interp(t, np.arange(len(x)), x) > 0).astype(np.uint8)
    return bits, rate / period


def count_markers(bits: np.ndarray) -> int:
    marker = np.array([(ASM >> (31 - i)) & 1 for i in range(32)], dtype=np.uint8)
    windows = np.lib.stride_tricks.sliding_window_view(bits, 32)
    return int(np.count_nonzero(np.count_nonzero(windows != marker, axis=1) == 0))


def generate(work: Path) -> tuple[bytes, str]:
    bits, baud = demodulate(fetch(work))
    counts = {"그대로": count_markers(bits), "뒤집어": count_markers(1 - bits)}
    polarity = max(counts, key=counts.get)
    if counts[polarity] == 0:
        raise SystemExit("ASM 을 한 번도 찾지 못했다 — 복조가 틀렸다")
    chosen = bits if polarity == "그대로" else 1 - bits
    usable = len(chosen) // 8 * 8
    report = f"{baud:.1f} baud · {len(chosen)} 비트 · 극성 {polarity} · 오류 없는 ASM {counts[polarity]} 개"
    return np.packbits(chosen[:usable]).tobytes(), report


def fetch_eirsat(work: Path) -> Path:
    path = work / "eirsat1_12324560.ogg"
    if not path.exists():
        work.mkdir(parents=True, exist_ok=True)
        urllib.request.urlretrieve(EIRSAT_URL, path)
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if digest != EIRSAT_SHA256:
        raise SystemExit(f"EIRSAT-1 녹음의 sha256 이 다르다: {digest}")
    return path


def satnogs_frames() -> bytes:
    """SatNOGS 지상국이 같은 관측에서 복호해 올린 프레임 — 마스터 프레임 카운트 순으로 잇는다."""
    import json
    observation = json.load(urllib.request.urlopen(
        f"https://network.satnogs.org/api/observations/{EIRSAT_OBSERVATION}/?format=json", timeout=60))
    frames = [urllib.request.urlopen(d["payload_demod"], timeout=60).read() for d in observation["demoddata"]]
    if any(len(f) != EIRSAT_FRAME for f in frames):
        raise SystemExit("SatNOGS 프레임 길이가 892 가 아니다")
    return b"".join(sorted(frames, key=lambda f: f[2]))


def eirsat_cadus(path: Path) -> tuple[bytes, str]:
    import soundfile
    x, rate = soundfile.read(str(path))
    x = x - np.convolve(x, np.ones(481) / 481, mode="same")
    marker = np.array([(ASM >> (31 - i)) & 1 for i in range(32)], dtype=np.uint8)
    nominal = rate / NOMINAL_BAUD
    window, hop, span = 3 * rate, rate // 2, (32 + EIRSAT_CODEBLOCK * 8)
    found: dict[int, np.ndarray] = {}
    for start in range(0, len(x) - window, hop):
        segment = x[start:start + window]
        n = np.arange(len(segment) - 1)
        c = n[(segment[:-1] < 0) != (segment[1:] < 0)]
        if len(c) < 1000:
            continue
        crossings = c + segment[c] / (segment[c] - segment[c + 1])
        periods = np.linspace(nominal * 0.99, nominal * 1.01, 1001)
        coherence = np.array([np.mean(np.exp(2j * np.pi * crossings / p)) for p in periods])
        best = int(np.argmax(np.abs(coherence)))
        if abs(coherence[best]) < 0.3:      # 잡음뿐인 창 — 교차가 격자에 모이지 않는다
            continue
        period = periods[best]
        boundary = (np.angle(coherence[best]) / (2 * np.pi)) * period % period
        t = np.arange(boundary + period / 2, len(segment) - 1, period)
        bits = (np.interp(t, np.arange(len(segment)), segment) > 0).astype(np.uint8)
        for polarity in (bits, 1 - bits):
            windows = np.lib.stride_tricks.sliding_window_view(polarity, 32)
            for k in np.nonzero(np.count_nonzero(windows != marker, axis=1) <= 3)[0]:
                if k + span > len(polarity):
                    continue
                at = start + int(t[k])
                if any(abs(at - seen) < rate // 10 for seen in found):   # 겹친 창이 같은 CADU 를 또 찾은 것
                    continue
                found[at] = polarity[k:k + span]
    stream = np.concatenate([found[at] for at in sorted(found)])
    return np.packbits(stream).tobytes(), f"EIRSAT-1: CADU {len(found)} 장 ({len(stream) // 8} 바이트)"


def compare(path: Path, data: bytes, check: bool) -> bool:
    if not check:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        print(f"썼다: {path.relative_to(ROOT)} ({len(data)} 바이트)")
        return True
    current = path.read_bytes() if path.exists() else b""
    if current != data:
        print(f"다시 만든 것과 다르다: {path.relative_to(ROOT)}", file=sys.stderr)
        return False
    print(f"재현됨: {path.relative_to(ROOT)} ({len(data)} 바이트)")
    return True


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="다시 만들어 저장소 파일과 대조한다")
    ap.add_argument("--work", default=str(ROOT / "build" / "capture"))
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    work = Path(args.work)
    data, report = generate(work)
    print(report)
    ok = compare(GOLDEN, data, args.check)

    # archive.org · SatNOGS 는 GitHub 보다 자주 느리거나 멈춘다 — 받지 못한 것은 "건너뜀" 으로 알리고, 받았는데 다르면 실패한다.
    try:
        recording = fetch_eirsat(work)
        frames = satnogs_frames()
    except OSError as error:
        print(f"EIRSAT-1 자료를 받지 못해 건너뛴다: {error}")
        return 0 if ok else 1
    cadus, report = eirsat_cadus(recording)
    print(report)
    ok &= compare(EIRSAT_CADUS, cadus, args.check)
    ok &= compare(EIRSAT_SATNOGS, frames, args.check)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
