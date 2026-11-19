#!/usr/bin/env python3
"""베이스 마커 인쇄물 생성 (표준 라이브러리만 사용).

출력
  marker_<id>.png        Unity XRReferenceImageLibrary 에 넣을 이미지 (Reference Image 이름 = <id>)
  marker_<id>_print.svg  A4 인쇄용. 마커가 정확히 --size-cm 크기로, 아래에 10cm 자가 함께 인쇄된다.

이미지 트래킹은 특징점이 많고 반복이 없는 이미지가 잘 잡힌다. 무작위(시드 고정) 블록 패턴 + 굵은 테두리.
인쇄 후 반드시 자로 실측해서 앱의 '마커 cm' 와 Reference Image 의 Physical Size 에 같은 값을 넣는다.

  python3 tools/make_marker.py --id base --size-cm 20
"""
import argparse, base64, random, struct, zlib


def png_bytes(pixels, w, h):
    raw = b"".join(b"\x00" + bytes(pixels[y * w:(y + 1) * w]) for y in range(h))
    def chunk(t, d):
        c = struct.pack(">I", len(d)) + t + d
        return c + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 0, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def make_pattern(seed, n=16, px=1024):
    rng = random.Random(seed)
    cell = px // n
    img = [255] * (px * px)
    border = 1  # 바깥 1칸은 검정 테두리
    for cy in range(n):
        for cx in range(n):
            if cx < border or cy < border or cx >= n - border or cy >= n - border:
                v = 0
            else:
                v = rng.choice((0, 0, 255, 255, 96, 176))
            for y in range(cy * cell, (cy + 1) * cell):
                row = y * px
                for x in range(cx * cell, (cx + 1) * cell):
                    img[row + x] = v
    # 비대칭 표식: 왼쪽 위 안쪽에 흰 원 (방향 구분 + 특징 추가)
    r = cell * 1.2
    cx0, cy0 = cell * 2.5, cell * 2.5
    for y in range(int(cy0 - r), int(cy0 + r)):
        for x in range(int(cx0 - r), int(cx0 + r)):
            if (x - cx0) ** 2 + (y - cy0) ** 2 <= r * r:
                img[y * px + x] = 255
    return img, px


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--id", default="base")
    ap.add_argument("--size-cm", type=float, default=20.0)
    ap.add_argument("--seed", type=int, default=7364)
    a = ap.parse_args()

    img, px = make_pattern(a.seed)
    png = png_bytes(img, px, px)
    with open(f"marker_{a.id}.png", "wb") as f:
        f.write(png)

    size_mm = a.size_cm * 10
    x0 = (210 - size_mm) / 2
    y0 = 20
    ruler_y = y0 + size_mm + 15
    ticks = "".join(
        f'<line x1="{x0 + i}" y1="{ruler_y}" x2="{x0 + i}" y2="{ruler_y + (6 if i % 10 == 0 else 3)}" stroke="black" stroke-width="0.2"/>'
        for i in range(0, 101))
    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" width="210mm" height="297mm" viewBox="0 0 210 297">
  <image x="{x0}" y="{y0}" width="{size_mm}" height="{size_mm}" href="data:image/png;base64,{base64.b64encode(png).decode()}" style="image-rendering:pixelated"/>
  <line x1="{x0}" y1="{ruler_y}" x2="{x0 + 100}" y2="{ruler_y}" stroke="black" stroke-width="0.3"/>
  {ticks}
  <text x="{x0}" y="{ruler_y + 12}" font-size="4">10 cm 자 — 인쇄 후 실측해서 확인 (배율 100%, 용지 맞춤 끄기)</text>
  <text x="{x0}" y="{ruler_y + 18}" font-size="4">marker id: {a.id} / 설계 크기 {a.size_cm} cm / 실측: ______ cm × ______ cm</text>
</svg>'''
    with open(f"marker_{a.id}_print.svg", "w", encoding="utf-8") as f:
        f.write(svg)
    print(f"wrote marker_{a.id}.png, marker_{a.id}_print.svg ({a.size_cm} cm)")


if __name__ == "__main__":
    main()
