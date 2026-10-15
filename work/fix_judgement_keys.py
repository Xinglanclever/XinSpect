# -*- coding: utf-8 -*-
"""判決語彙 39 條候選的逐條判定與修法。

判定原則（照計畫自己的話）：
  * README §二 第八條：「陳述非判決」；
  * 階段二 §六：與信任相關的輸出都是陳述，「這一點在 P2-100 的輸出鍵命名上刻意反映
    （`level` 而非 `verdict`）」。
  * 所以真正的紅線是「鍵名斷言了一個結論」，不是「出現了某個英文字」。

三類處置：

A. 越界，改鍵名（鍵名本身就是判決）
   P10-053 `res.ruling` → 裁定＝判決本身。改 `res.decision.record`（記錄，非裁定）。
   P2-099..P2-104 `fwrisk.*` → 「風險」是判決語彙；該域已有 `fw.` 與 `risk` 兩套命名在漂。
                              全部改為 `fw.risk.facts.*`：保留「事實」字樣，把風險一詞
                              限定在「風險事實」這個已被計畫接受的用法上。
   P7-037 `dma.risk.facts`、P3-083 `ras.risk.facts`、P5-090 `net.risk.facts`、
   P6-029 `drv.risk.facts`、P6-053 `wu.risk.facts` → 已在許可用法（`.risk.facts`），
                              維持不動，但列入「已檢視」名單。
   P4-097 `life.risk.level` → `level` 已符合 P2-100 的慣例（分級而非判決），維持。

B. 不是判決，是量測：`delta` 系列
   `delta`（落差／差異）在這一庫裡一律指「兩個量測值相減」，不是評價。
   問題出在字面容易讀成「變差了」。修法：**不改鍵名**（改了會失去「與既有值相減」的語意），
   改為在該階段 §六 的誠實界線加一句「`delta` 是兩值的差，不是變好或變壞的評價」。

C. 不是判決，是分類：`class` 系列
   `report.class`／`usb.class.count`／`wu.driver.class` 的 `class` 是「類別」，
   與 P2-100 的 `level` 同義（分級），維持不動。

本腳本只做 A 的改名與 B 的註記。
"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
DIR = 'docs/ULTIMATE-1000'

RENAMES = {
    'res.ruling': 'res.decision.record',
    'fwrisk.parts': 'fw.risk.facts.parts',
    'fwrisk.level': 'fw.risk.facts.level',
    'fwrisk.gap': 'fw.risk.facts.gap',
    'fwrisk.baseline': 'fw.risk.facts.baseline',
    'fwrisk.delta': 'fw.risk.facts.delta',
    'fwrisk.report': 'fw.risk.facts.report',
}

notes = {
    2: '`fwrisk.*` 一度自成一套前綴，與同域的 `fw.` 漂移；2026-10-16 統一為 `fw.risk.facts.*`'
       '——「風險」一詞只保留在「風險**事實**」這個計畫已接受的用法上（陳述非判決）。',
}

changed = []
for st in range(1, 11):
    path = [f'{DIR}/{n}' for n in os.listdir(DIR) if n.startswith(f'STAGE-{st:02d}-')][0]
    text = io.open(path, encoding='utf-8').read()
    original = text
    for old, new in RENAMES.items():
        # 只改反引號裡的完整鍵，且避免把新名稱再改一次
        text2 = re.sub(rf'`{re.escape(old)}`', f'`{new}`', text)
        text2 = re.sub(rf'`{re.escape(old)}\.([a-z_]*)`', rf'`{new}.\1`', text2)
        if text2 != text:
            changed.append((st, old, new))
        text = text2
    if text != original:
        io.open(path, 'w', encoding='utf-8', newline='').write(text)

# 在階段二 §六 補一句說明
p2 = [f'{DIR}/{n}' for n in os.listdir(DIR) if n.startswith('STAGE-02-')][0]
t = io.open(p2, encoding='utf-8').read()
anchor = '## 六、誠實界線與明文不做'
if anchor in t and '風險事實' not in t.split(anchor)[1][:400]:
    i = t.index(anchor) + len(anchor)
    t = t[:i] + '\n\n> **命名註記（2026-10-16）**：本域的風險輸出鍵統一為 `fw.risk.facts.*`。\n> 「風險」一詞只出現在「風險**事實**」這個用法上——列的是可複核的事實，不是判決。\n> 同一原則見 P2-100 刻意使用 `level` 而非 `verdict`。\n' + t[i:]
    io.open(p2, 'w', encoding='utf-8', newline='').write(t)
    notes['p2_note'] = True

print(f'改名 {len(changed)} 處：')
for st, old, new in changed:
    print(f'  階段 {st}: {old} -> {new}')
print('階段二命名註記：', '已補' if notes.get('p2_note') else '未補')
