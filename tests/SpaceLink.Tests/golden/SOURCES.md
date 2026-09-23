# 시험 자료의 출처

| 파일 | 무엇 | 출처 · 라이선스 |
|---|---|---|
| `libfec_rs_vectors.txt` | libfec(Phil Karn)의 CCSDS RS 부호화·복호 결과 | `tools/gen_golden_libfec.py` 가 libfec 고정 커밋을 빌드해 만든다. libfec 코드는 넣지 않았다(LGPL) — 이 파일은 출력 데이터다 |
| `astrocast_9k6_bits.bin` | Astrocast 0.1 (NORAD 43798) 9k6 FSK 녹음을 복조한 비트열 | 녹음: DK3WN, [satellite-recordings](https://github.com/daniestevez/satellite-recordings) `ea2bd26` — Unlicense(퍼블릭 도메인) |
| `eirsat1_cadus.bin` | EIRSAT-1 (NORAD 58472) 9k6 GMSK 녹음에서 복조기가 찾은 CADU 46 장(경판정 비트) | 녹음: [SatNOGS 관측 12324560](https://network.satnogs.org/observations/12324560/) (EA5WA 지상국, 2025-09-04) — **CC BY-SA 4.0** |
| `eirsat1_satnogs_frames.bin` | 같은 관측에서 SatNOGS 지상국이 복호해 올린 전송 프레임 26 장(마스터 카운트 순) | SatNOGS 관측 12324560 의 demoddata — **CC BY-SA 4.0** |

CC BY-SA 4.0 자료(`eirsat1_*`)는 출처를 밝혀 쓰고, 이 두 파일을 고쳐 배포하면 같은 라이선스를 따른다. 저장소 코드의 MIT 라이선스(`LICENSE`)는 이 폴더의 자료에 적용되지 않는다.
세 녹음 파생 파일은 `python tools/gen_golden_capture.py` 로 다시 만들 수 있고, CI 가 다시 만들어 대조한다.
