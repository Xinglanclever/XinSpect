# -*- coding: utf-8 -*-
"""檢查我這一輪新寫的服務，有沒有撞到既有服務的鍵或語意。

P2-052 的教訓：我寫了一個平行的 FIT 解碼器，而
Services/SpiFitFactsService.cs 早就有 FitDecoder 了。
這支腳本把那種情況機械化地找出來。

═══════════════════════════════════════════════════════════════════════
結果（2026-10-16）：**鍵撞名掃描有效（0 命中＝真的沒撞），
但語意掃描抓不到 P2-052 那個情況。**

因為它是**字面的**——比對鍵名與中文詞。
而我之所以錯過 FIT，是因為我根本沒去查「FIT 有沒有解碼器」，
不是因為名稱相近。**掃「名字」抓不到「掃能力」該抓的東西。**

所以它只保留為**鍵撞名的守門**（那部分是真的有用：
同一把鍵有兩個產生者一定是錯的）。
「動手前先查既有解碼器」的規則寫在 audit_todo_against_existing.py 的檔頭。
═══════════════════════════════════════════════════════════════════════
"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

ROOT = r'C:\Users\Administrator\XinSpect\Services'

MINE = {
    'SmbiosProcessorFactsService', 'SmbiosIdentityFactsService',
    'SmbiosTpmFactsService', 'SmbiosStructureFactsService',
    'SmbiosPowerFactsService', 'SmbiosCrossFactsService',
    'FirmwareModeFactsService', 'UefiBootOrderFactsService',
    'BootFastStartupFactsService', 'FirmwareSettingsReachFactsService',
    'MicrocodeVersionFactsService', 'OptionRomFactsService',
    'AcpiTpm2WpbtFactsService',
}

# ① 既有服務產生的事實鍵
old_keys = {}
# ② 既有服務的類別名與 XML 摘要首句（用來比對語意）
old_classes = {}

key_re = re.compile(r'new\s+HardwareFact\(\s*"([a-z0-9_.]+)"')
class_re = re.compile(r'public\s+static\s+class\s+(\w+)')
summary_re = re.compile(r'///\s*<summary>\s*\n\s*///\s*(.+)')

for fn in sorted(os.listdir(ROOT)):
    if not fn.endswith('.cs'):
        continue
    name = fn[:-3]
    if name in MINE:
        continue
    text = io.open(os.path.join(ROOT, fn), encoding='utf-8', errors='replace').read()
    for m in key_re.finditer(text):
        old_keys.setdefault(m.group(1), set()).add(name)
    cm = class_re.search(text)
    sm = summary_re.search(text)
    if cm:
        old_classes[cm.group(1)] = (name, sm.group(1).strip() if sm else '')

print('=== ① 鍵直接撞名 ===')
collisions = 0
for mn in sorted(MINE):
    p = os.path.join(ROOT, mn + '.cs')
    if not os.path.exists(p):
        print(f'  {mn}：(檔案不存在)')
        continue
    t = io.open(p, encoding='utf-8', errors='replace').read()
    keys = set(re.findall(r'const\s+string\s+\w*Key\w*\s*=\s*"([a-z0-9_.]+)"', t))
    hits = {k: sorted(old_keys[k]) for k in keys if k in old_keys}
    if hits:
        collisions += len(hits)
        print(f'  {mn}：')
        for k, v in hits.items():
            print(f'      {k}  ← 已存在於 {v}')
if collisions == 0:
    print('  （無）')

print()
print('=== ② 類別或檔名語意相近（可能是平行實作）===')
near = 0
for mn in sorted(MINE):
    p = os.path.join(ROOT, mn + '.cs')
    if not os.path.exists(p):
        continue
    t = io.open(p, encoding='utf-8', errors='replace').read()
    sm = summary_re.search(t)
    first = sm.group(1).strip() if sm else ''
    # 用「關鍵詞集合重疊」比對既有服務的摘要
    words = set(re.findall(r'[\u4e00-\u9fff]{2,}', first))
    for cls, (ofn, osm) in old_classes.items():
        owords = set(re.findall(r'[\u4e00-\u9fff]{2,}', osm))
        common = words & owords
        # 門檻：至少 4 個共同的雙字詞，且不是隨處可見的通用詞
        common -= {'韌體', '服務', '事實', '唯讀', '三態'}
        if len(common) >= 4:
            near += 1
            print(f'  {mn} ↔ {ofn}')
            print(f'      我的：{first[:70]}')
            print(f'      既有：{osm[:70]}')
            print(f'      共同詞：{"、".join(sorted(common))}')
if near == 0:
    print('  （無）')
