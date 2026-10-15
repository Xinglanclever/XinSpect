# -*- coding: utf-8 -*-
"""第 100 項發版（續）：把三份 README 的下載連結、註腳與版本沿革同步到 2.85。

ChangelogTests 抓到四個地方還沒跟上，而它們是四種不同的東西：
  ① 下載連結的 tag（releases/download/v2.84/...）
  ② 下載表的註腳（「本版（v2.84）」）
  ③ 版本沿革的最新一筆（要與 ChangelogCatalog 的第一筆對得上）
  ④ 某處的「版本 2.84 ・」字串

這四處都是「改版號時容易漏掉的地方」——而它們有守門，所以漏了會紅。
"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

OLD = "2.84"
NEW = "2.85"

TITLE = "ULTIMATE-1000 階段一：契約、骨架、出處鏈與守門方法論（105 項）"

SUMMARY = (
    "掃描骨架契約（逾時必先宣告、還原契約、中斷保留）／來源接縫（狀態分離、全鏈留痕、"
    "雙向對帳）／六態收斂與格的成文／出處鏈八把鍵／知識包與知識表三態查詢／"
    "敏感度 L0–L3 分級與 L3 禁項掃描／寫入閘門與審計鏈互驗／出網四種觸發只有主動允許／"
    "韌性與降級申報／守門方法論（五模式、等級宣告含守不住什麼、誤報要修守門不是放寬）／"
    "發佈與對外文件。測試 5848 綠、事實鍵目錄 930 把（＋23 個動態家族）。"
)

for readme in ('README.md', 'README.en.md', 'README.zh-CN.md'):
    t = io.open(readme, encoding='utf-8').read()
    before = t

    # ①② 下載連結與註腳
    t = t.replace(f"v{OLD}/", f"v{NEW}/")
    t = t.replace(f"（v{OLD}）", f"（v{NEW}）")
    t = t.replace(f"(v{OLD})", f"(v{NEW})")
    t = t.replace(f"版本 {OLD} ・", f"版本 {NEW} ・")

    # ③ 版本沿革：在最新一筆之前插入 2.85
    lines = t.split('\n')
    for i, line in enumerate(lines):
        if re.match(rf'^\s*-\s*\*\*v{re.escape(OLD)}', line):
            indent = line[:len(line) - len(line.lstrip())]
            newline = f"{indent}- **v{NEW} Ultima I**（2026-10-16）：{TITLE}——{SUMMARY}"
            lines.insert(i, newline)
            break
    t = '\n'.join(lines)

    if t != before:
        io.open(readme, 'w', encoding='utf-8', newline='').write(t)
        print(f'{readme}：已更新')
    else:
        print(f'{readme}：沒有任何改動')
