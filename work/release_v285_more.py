# -*- coding: utf-8 -*-
"""第 100 項發版（續二）：補上兩處漏掉的版號。

ChangelogTests 的守門指出兩個我漏掉的地方，它們分別代表兩種「版號住在哪」：

  ① Views/AboutView.xaml 的「版本 2.84 ・」——這是程式畫面直接顯示的字串，
     不在 README 裡，所以容易漏。守門把它與 ChangelogCatalog.Latest 對比。
     （三處版號：csproj／README 徽章／AboutView——這正是「三處」的意思。）

  ② README.en.md 第 16 行的下載連結與標題（`releases/tag/v2.84`）——英文版的下載
     區塊格式與中文版不同（中文版是表格裡的 download/v2.84/，英文版是 tag/v2.84），
     所以只替換 `v2.84/` 抓不到它。
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')

OLD = "2.84"
NEW = "2.85"

# ① AboutView.xaml
p = 'Views/AboutView.xaml'
t = io.open(p, encoding='utf-8').read()
old = f'Text="版本 {OLD} ・ Olympus ・ 便攜單一執行檔"'
new = f'Text="版本 {NEW} ・ Ultima I ・ 便攜單一執行檔"'
if old in t:
    t = t.replace(old, new)
    io.open(p, 'w', encoding='utf-8', newline='').write(t)
    print('AboutView.xaml：已更新（含代號 Olympus → Ultima I）')
else:
    print('!! AboutView.xaml 找不到目標字串')
    for line in t.split('\n'):
        if '版本 2.' in line:
            print('   實際：', line.strip())

# ② README.en.md 的下載連結與標題
p = 'README.en.md'
t = io.open(p, encoding='utf-8').read()
n = 0
for old, new in (
    (f'releases/tag/v{OLD}', f'releases/tag/v{NEW}'),
    (f'Download v{OLD} Olympus', f'Download v{NEW} Ultima I'),
):
    if old in t:
        t = t.replace(old, new)
        n += 1
if n:
    io.open(p, 'w', encoding='utf-8', newline='').write(t)
    print(f'README.en.md：更新 {n} 處')
else:
    print('!! README.en.md 找不到目標字串')
    for line in t.split('\n'):
        if 'v2.84' in line:
            print('   實際：', line.strip()[:120])
