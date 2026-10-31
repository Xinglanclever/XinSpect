#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""從品牌插畫產生 Assets/app.ico（多尺寸 PNG payload，Vista 之後的 .ico）。

來源：Assets/brand-source.jpg —— 使用者提供的品牌圖（藍色 X ＋ 兩張視窗卡）。
這是唯一需要人工挑參數的地方：裁切框以「藍色 X ＋ 視窗」的外框為準，
正方形化時留 6% 邊距，四角做成 20% 圓角——與舊版圖示的圓角比例一致。

用法：
    python Tools/make-icon.py            # 重新產生 Assets/app.ico
    python Tools/make-icon.py --preview  # 順便把 1024/256/32 的 PNG 寫到 obj/ 供檢視

為什麼是 Python 而不是原本的 System.Drawing 向量繪圖：新的來源是一張點陣插畫，
要的是「高品質縮放 ＋ 多尺寸打包」，PIL 的 ICO 寫入器一次做完，而且每個尺寸
都是從 1024 母圖重取樣，不是拿 256 再放大。
"""

import argparse
import os
import sys

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, "Assets", "brand-source.jpg")
OUT = os.path.join(ROOT, "Assets", "app.ico")

# 插畫外框（在 1254×1254 的原圖上量的）：藍色 X 的 bbox 是 x 198–1022 / y 133–810，
# 下方的 wordmark 從 y≈820 開始，所以上界停在 810 才不會把字切進來。
ART_BOX = (198, 1022, 133, 810)
MARGIN = 0.06          # 正方形化時留的邊距（相對邊長）
CORNER = 0.20          # 圓角半徑（相對邊長），沿用舊圖示的比例
MASTER = 1024          # 母圖邊長
SIZES = [256, 128, 64, 48, 32, 16]


def build_master():
    src = Image.open(SOURCE).convert("RGB")
    x0, x1, y0, y1 = ART_BOX
    cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
    side = int(max(x1 - x0, y1 - y0) * (1.0 + MARGIN))
    left, top = int(cx - side / 2), int(cy - side / 2)
    sq = src.crop((left, top, left + side, top + side)).resize((MASTER, MASTER), Image.LANCZOS)

    mask = Image.new("L", (MASTER, MASTER), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, MASTER - 1, MASTER - 1], radius=int(MASTER * CORNER), fill=255)
    icon = Image.new("RGBA", (MASTER, MASTER), (0, 0, 0, 0))
    icon.paste(sq, (0, 0), mask)
    return icon, (left, top, left + side, top + side)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview", action="store_true", help="把 1024/256/32 的 PNG 寫到 obj/")
    args = ap.parse_args()

    if not os.path.exists(SOURCE):
        sys.exit("找不到 %s —— 品牌圖不在，無法產生圖示。" % os.path.relpath(SOURCE, ROOT))

    icon, box = build_master()
    # PIL 的 ICO 寫入器會對每個尺寸重新取樣，並以 PNG 存內嵌資料（>= 256 一定是 PNG）。
    icon.save(OUT, format="ICO", sizes=[(s, s) for s in SIZES])

    print("寫入 %s（來源 %s，裁切 %s）" % (
        os.path.relpath(OUT, ROOT), os.path.relpath(SOURCE, ROOT), box))

    # 讀回來確認真的有多尺寸——寫入器不支援時會靜默只留一張。
    with Image.open(OUT) as ico:
        got = sorted(ico.info.get("sizes", []))
    print("內含尺寸：%s" % ", ".join("%d×%d" % s for s in got))
    missing = [s for s in SIZES if (s, s) not in got]
    if missing:
        sys.exit("缺少尺寸：%s" % missing)

    if args.preview:
        os.makedirs(os.path.join(ROOT, "obj"), exist_ok=True)
        icon.resize((256, 256), Image.LANCZOS).save(os.path.join(ROOT, "obj", "icon-256.png"))
        icon.resize((32, 32), Image.LANCZOS).resize((256, 256), Image.NEAREST).save(
            os.path.join(ROOT, "obj", "icon-32x8.png"))
        print("預覽：obj/icon-256.png、obj/icon-32x8.png")


if __name__ == "__main__":
    main()
