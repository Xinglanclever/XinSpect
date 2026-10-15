# -*- coding: utf-8 -*-
"""把兩條守門的「免除正對照」理由補進 GuardRegistry。

背景：GuardPolicy.Validate 現在區分「沒有正對照」與「不需要正對照」——
後者必須附至少 30 字的理由，否則免除會變成預設值，而預設免除等於預設鬆散。
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Services/GuardRegistry.cs'
t = io.open(p, encoding='utf-8').read()

old1 = """                "『用字串表示狀態』——字串沒有型別可以掃，那要由介面契約擋"),
            HasPositiveControl: false),"""
new1 = """                "『用字串表示狀態』——字串沒有型別可以掃，那要由介面契約擋"),
            HasPositiveControl: false,
            PositiveControlExemption:
                "這條的樣式若失效會回空集合，而另一條『六態必須是唯一的列舉定義處』"
                + "會獨立抓到定義重複——兩條互為對方的正對照，任何一條失效另一條仍會亮。"),"""

old2 = """                "『另一種表達同一組狀態的型別』——那由單一可用性型別那條守門管"),
            HasPositiveControl: false),"""
new2 = """                "『另一種表達同一組狀態的型別』——那由單一可用性型別那條守門管"),
            HasPositiveControl: false,
            PositiveControlExemption:
                "這是計數斷言（定義處必須恰好一個），沒有樣式失效的空間："
                + "把要抓的字串複製進任何檔案都會讓計數變成 2，也就是它必然會紅。"),"""

n = 0
for old, new in ((old1, new1), (old2, new2)):
    if old in t:
        t = t.replace(old, new)
        n += 1
    else:
        print('!! 找不到：', old.split('\n')[0][:50])

io.open(p, 'w', encoding='utf-8', newline='').write(t)
print(f'補了 {n} 條免除理由')
