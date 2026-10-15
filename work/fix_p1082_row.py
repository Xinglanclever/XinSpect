# -*- coding: utf-8 -*-
"""修正 P1-082 的矩陣列：鍵名與驗收條件都要跟著實作更新。

兩處修正，都是「實作改了、規格沒跟上」這一類的真實漂移：

① 鍵名 `exempt` → `coverage.exempt`
   `exempt` 是單段鍵，違反全鍵格式（須以點分段）。這一條是 P1-084 的
   命名規範守門抓到的，而它抓到的是我加鍵的下一分鐘。正解不是放寬格式
   而是改鍵名——豁免確實是覆蓋申報的子項，本來就該在那個前綴底下。

② 驗收條件『理由限「無法交叉」』→『理由須指名一個正當性類別』
   原本那句話把門檻寫成「理由的文字要長什麼樣」，而那個門檻是不成立的：
   584 條真實豁免裡有 507 條不含「無法交叉」這幾個字，而它們的理由是實質正確的。
   我當時的處置是連續擴大關鍵詞表——那正是 P1-063 警告的事。
   正確的門檻是「指名一個有限的類別」，因為待辦選不出任何合法類別。
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'docs/ULTIMATE-1000/STAGE-01-FOUNDATION.md'
t = io.open(p, encoding='utf-8').read()

old = '| P1-082 | 豁免理由門檻 | 理由限「無法交叉」 | `exempt` | L0 | S | P1-081 |'
new = '| P1-082 | 豁免理由門檻 | 理由須指名一個正當性類別 | `coverage.exempt` | L0 | S | P1-081 |'

if old in t:
    t = t.replace(old, new)
    io.open(p, 'w', encoding='utf-8', newline='').write(t)
    print('已更新 P1-082 矩陣列')
else:
    print('!! 找不到 P1-082 那一行')
    for line in t.split('\n'):
        if line.startswith('| P1-082'):
            print('實際：', line)
