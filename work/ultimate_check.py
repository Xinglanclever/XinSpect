# -*- coding: utf-8 -*-
"""ULTIMATE-1000 結構對帳（委派版）。

這支腳本已經**不再是**對帳的依據。它留在樹上是為了留下事件紀錄：
2026-10-16 的第一輪稽核就是用這一組方法做的，而它建立在「字元類別 ＋ 量詞」
的比對樣式上（用 `[A-Z]+\\d?` 認域碼）。那個樣式在本機的兩個正則引擎上都
給出錯誤答案，於是階段一的 F10 一整列認不出來，接著被誤讀成
「階段一宣告 95 項、規格有缺陷」。

真正可引為證據的對帳工具是 C# 寫的 `work/UltimateAudit/`：它開頭先跑自我檢查
與環境探針，任一項不符就以離開碼 2 結束、不產出結論。
本腳本只負責呼叫它並轉述結果——不自己算任何數字。
"""
import io
import subprocess
import sys

# 主控台代碼頁（本機是 cp950）容不下所有字元，輸出前先換成可容忍的編碼。
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

proc = subprocess.run(
    ['dotnet', 'run', '-c', 'Release', '--project', 'work/UltimateAudit', '--', '.'],
    capture_output=True, text=True, encoding='utf-8', errors='replace',
)

sys.stdout.write(proc.stdout or '')
if proc.returncode == 2:
    print('\n稽核工具自我檢查失敗——它的結論不可信，請先修工具。')
elif proc.returncode != 0:
    print(f'\n稽核工具回報結構不符（離開碼 {proc.returncode}），詳見上方輸出。')

sys.exit(proc.returncode)
