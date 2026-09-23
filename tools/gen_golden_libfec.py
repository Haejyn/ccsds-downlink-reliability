#!/usr/bin/env python3
"""libfec(Phil Karn) 의 CCSDS RS(255,223) 로 기준 벡터를 만든다 — 이 저장소와 **다른 사람이 쓴** 구현과 바이트 단위로 대조하려고.

부속서 G 계수표(§9.7)·가상 채움의 정의(§9.8)는 표준 문서와의 대조다. 이 벡터는 거기에 더해, 지상국 소프트웨어에서 널리 쓰는
공개 구현의 **실제 출력**과 대조한다 — 생성 다항식·이중 기저·바이트 안의 비트 배치·짧은 코드블록을 한 번에 확인한다.

- libfec 는 저장소에 넣지 않는다(LGPL). 고정 커밋을 받아 빌드해 벡터 생성에만 쓰고, 벡터(데이터)만 커밋한다.
- 하네스 `tools/libfec_vectors/vectors.c` 는 이 저장소의 코드다 — libfec 의 encode_rs_ccsds / decode_rs_ccsds 를 부른다.
- 입력은 고정 시드로 만든다. 같은 커밋·같은 시드면 같은 파일이 나온다.

사용:
  python tools/gen_golden_libfec.py            # 벡터를 다시 만들어 저장소에 쓴다
  python tools/gen_golden_libfec.py --check    # 다시 만들어 저장소 파일과 대조한다(CI). 다르면 종료 코드 1
C 컴파일러는 CC 환경 변수 또는 --cc 로 준다(기본 gcc). git 이 있어야 한다.
"""
import argparse
import os
import random
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GOLDEN = ROOT / "tests" / "SpaceLink.Tests" / "golden" / "libfec_rs_vectors.txt"
HARNESS = ROOT / "tools" / "libfec_vectors" / "vectors.c"
LIBFEC_URL = "https://github.com/quiet/libfec.git"   # Phil Karn 의 libfec 를 x86-64 에서 빌드되게 고친 복제본
LIBFEC_COMMIT = "9750ca0a6d0a786b506e44692776b541f90daa91"
SEED = 20260923
PADS = (0, 1, 95, 100, 222)   # 가상 채움 Q (I = 1). 95 는 128 바이트 프레임


def run(cmd, **kw):
    return subprocess.run([str(c) for c in cmd], check=True, **kw)


def build(cc: str, work: Path) -> Path:
    src = work / "libfec"
    if not (src / "encode_rs_ccsds.c").exists():
        shutil.rmtree(src, ignore_errors=True)
        run(["git", "clone", "-q", LIBFEC_URL, src])
    run(["git", "-C", src, "checkout", "-q", LIBFEC_COMMIT])
    out = work / "out"
    out.mkdir(parents=True, exist_ok=True)
    exe = ".exe" if os.name == "nt" else ""
    # 표 생성기 둘 — libfec 의 makefile 과 같은 순서
    run([cc, "-O2", "-I", src, "-o", out / f"gen_ccsds{exe}", src / "gen_ccsds.c", src / "init_rs_char_local.c"])
    (out / "ccsds_tab.c").write_text(run([out / f"gen_ccsds{exe}"], capture_output=True, text=True).stdout)
    run([cc, "-O2", "-I", src, "-o", out / f"gen_ccsds_tal{exe}", src / "gen_ccsds_tal.c"])
    (out / "ccsds_tal.c").write_text(run([out / f"gen_ccsds_tal{exe}"], capture_output=True, text=True).stdout)
    harness = out / f"vectors{exe}"
    run([cc, "-O2", "-I", src, "-o", harness, HARNESS,
         src / "encode_rs_ccsds.c", src / "encode_rs_8.c", src / "decode_rs_ccsds.c", src / "decode_rs_8.c",
         out / "ccsds_tab.c", out / "ccsds_tal.c"])
    return harness


def ask(harness: Path, lines: list[str]) -> list[list[str]]:
    out = run([harness], input="\n".join(lines) + "\n", capture_output=True, text=True).stdout
    return [line.split() for line in out.splitlines() if line.strip()]


def generate(harness: Path) -> str:
    rnd = random.Random(SEED)
    rows: list[str] = []

    # ── 부호화: 패드마다 무작위 셋 + 모두 0 · 모두 FF · 첫 심볼만 1 · 끝 심볼만 1
    encode_inputs = []
    for pad in PADS:
        n = 223 - pad
        for kind in ("random", "random", "random", "zeros", "ones", "unit-first", "unit-last"):
            if kind == "random":
                data = bytes(rnd.randrange(256) for _ in range(n))
            elif kind == "zeros":
                data = bytes(n)
            elif kind == "ones":
                data = bytes([0xFF]) * n
            elif kind == "unit-first":
                data = bytes([1]) + bytes(n - 1)
            else:
                data = bytes(n - 1) + bytes([1])
            encode_inputs.append((kind, pad, data))
    encoded = ask(harness, [f"E {pad} {data.hex().upper()}" for _, pad, data in encode_inputs])
    codewords = {}
    for (kind, pad, data), (_, _, d_hex, p_hex) in zip(encode_inputs, encoded):
        rows.append(f"E {kind} {pad} {d_hex} {p_hex}")
        if kind == "random" and (pad, "cw") not in codewords:
            codewords[(pad, "cw")] = bytes.fromhex(d_hex + p_hex)

    # ── 복호: 무작위 부호어에 오류 t 개(보낸 자리에서만) — t = 0·1·8·16 은 정정, 17 은 정정 능력 밖
    decode_inputs = []
    for pad in PADS:
        cw = codewords[(pad, "cw")]
        for t in (0, 1, 8, 16, 17):
            received = bytearray(cw)
            for position in rnd.sample(range(len(cw)), t):
                received[position] ^= rnd.randrange(1, 256)
            decode_inputs.append((f"errors-{t}", pad, bytes(received)))

    # ── 복호: 정정 위치가 채움 자리로 나오는 수신어 (ShortenedCodeblockTests 와 같은 구성)
    #    c1 = 프레임의 짧은 부호어, c2 = 채움 자리(0)와 첫 데이터 자리(pad)에만 값이 있는 전체 부호어,
    #    받은 것 = c1 ⊕ c2 의 보낸 자리. 채움을 0 으로 되살리면 부호어 c1 ⊕ c2 와 채움 자리 하나만 다르다.
    pad = 95
    c1 = codewords[(pad, "cw")]
    e = bytearray(223)
    e[0], e[pad] = 0x5A, 0xC3
    (_, _, e_hex, e_par), = ask(harness, [f"E 0 {e.hex().upper()}"])
    c2 = bytes.fromhex(e_hex + e_par)
    decode_inputs.append(("fill-correction", pad, bytes(a ^ b for a, b in zip(c1, c2[pad:]))))

    decoded = ask(harness, [f"D {pad} {r.hex().upper()}" for _, pad, r in decode_inputs])
    for (kind, pad, received), (_, _, result, out_hex) in zip(decode_inputs, decoded):
        rows.append(f"D {kind} {pad} {received.hex().upper()} {result} {out_hex}")

    header = [
        "# libfec(Phil Karn) CCSDS RS(255,223) 기준 벡터 — tools/gen_golden_libfec.py 가 만든다. 손으로 고치지 말 것.",
        f"# libfec {LIBFEC_URL} @ {LIBFEC_COMMIT} · 시드 {SEED}",
        "# E <종류> <pad> <정보 심볼> <패리티 32>                          — encode_rs_ccsds (이중 기저 = 전송 바이트)",
        "# D <종류> <pad> <받은 부호어> <정정 수, -1 = 실패> <복호 뒤 부호어> — decode_rs_ccsds",
    ]
    return "\n".join(header + rows) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="다시 만들어 저장소 파일과 대조한다")
    ap.add_argument("--cc", default=os.environ.get("CC", "gcc"))
    ap.add_argument("--work", default=str(ROOT / "build" / "libfec"))
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    harness = build(args.cc, Path(args.work))
    text = generate(harness)
    if args.check:
        current = GOLDEN.read_text(encoding="utf-8") if GOLDEN.exists() else ""
        if current != text:
            print(f"기준 벡터가 libfec 로 다시 만든 것과 다르다: {GOLDEN.relative_to(ROOT)}", file=sys.stderr)
            return 1
        print(f"기준 벡터 재현됨 ({text.count(chr(10)) - 4} 줄)")
        return 0
    GOLDEN.parent.mkdir(parents=True, exist_ok=True)
    GOLDEN.write_text(text, encoding="utf-8", newline="\n")
    print(f"썼다: {GOLDEN.relative_to(ROOT)} ({text.count(chr(10)) - 4} 줄)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
