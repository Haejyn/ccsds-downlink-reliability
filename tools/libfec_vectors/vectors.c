/* libfec CCSDS RS(255,223) 로 기준 벡터를 만든다 — ccsds-downlink-reliability 의 독립 대조용.
 * 입력(표준 입력) 한 줄:  E <pad> <정보 심볼 16진수, 223-pad 바이트>
 *                        D <pad> <부호어 16진수, 255-pad 바이트>
 * 출력:                  E <pad> <정보> <패리티 32 바이트>
 *                        D <pad> <정정 수 또는 -1> <복호 뒤 부호어>
 * 모든 값은 이중 기저(전송 바이트) — libfec 의 encode_rs_ccsds / decode_rs_ccsds 가 변환을 맡는다. */
#include <stdio.h>
#include <string.h>
#include "fec.h"

static int unhex(const char *s, unsigned char *out, int max) {
  int n = 0;
  while (s[0] && s[1] && n < max) {
    unsigned v; if (sscanf(s, "%2x", &v) != 1) return -1;
    out[n++] = (unsigned char)v; s += 2;
  }
  return n;
}
static void hex(const unsigned char *b, int n) { for (int i = 0; i < n; i++) printf("%02X", b[i]); }

int main(void) {
  char line[4096], mode; int pad; char data_hex[1024];
  while (fgets(line, sizeof line, stdin)) {
    if (sscanf(line, " %c %d %1023s", &mode, &pad, data_hex) != 3) continue;
    unsigned char buf[255], parity[32];
    int n = unhex(data_hex, buf, 255);
    if (mode == 'E') {
      if (n != 223 - pad) { fprintf(stderr, "bad length %d for pad %d\n", n, pad); return 2; }
      encode_rs_ccsds(buf, parity, pad);
      printf("E %d ", pad); hex(buf, n); printf(" "); hex(parity, 32); printf("\n");
    } else if (mode == 'D') {
      if (n != 255 - pad) { fprintf(stderr, "bad length %d for pad %d\n", n, pad); return 2; }
      int r = decode_rs_ccsds(buf, NULL, 0, pad);
      printf("D %d %d ", pad, r); hex(buf, n); printf("\n");
    }
  }
  return 0;
}
