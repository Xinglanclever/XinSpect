# -*- coding: utf-8 -*-
"""第 100 項發版：同步測試基線、三份 README 徽章、版本號與變更記錄。

數字來源只有一個：dotnet test 跑完全套後印出的「測試數總計」＝5848。
不從紀錄裡的「+N」推算——本計畫吃過那個虧（5476→5505 實加 29，帳本寫 +31）。
"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

NEW_TESTS = 5848
OLD_TESTS = 5512
NEW_VERSION = "2.85"
OLD_VERSION = "2.84"

# ① 測試基線
p = 'Tests/TestSuiteBaseline.cs'
t = io.open(p, encoding='utf-8').read()
if f'ProjectTests = {NEW_TESTS};' in t:
    print(f'基線已是 {NEW_TESTS}')
else:
    t = t.replace(f'ProjectTests = {OLD_TESTS};', f'ProjectTests = {NEW_TESTS};')
    io.open(p, 'w', encoding='utf-8', newline='').write(t)
    print(f'測試基線 {OLD_TESTS} → {NEW_TESTS}')

# ② 三份 README 徽章
for readme in ('README.md', 'README.en.md', 'README.zh-CN.md'):
    t = io.open(readme, encoding='utf-8').read()
    changed = []
    if f'tests-{OLD_TESTS}%20passed' in t:
        t = t.replace(f'tests-{OLD_TESTS}%20passed', f'tests-{NEW_TESTS}%20passed')
        changed.append(f'tests {OLD_TESTS}→{NEW_TESTS}')
    if f'version-{OLD_VERSION}-' in t:
        t = t.replace(f'version-{OLD_VERSION}-', f'version-{NEW_VERSION}-')
        changed.append(f'version {OLD_VERSION}→{NEW_VERSION}')
    if changed:
        io.open(readme, 'w', encoding='utf-8', newline='').write(t)
        print(f'{readme}：' + '、'.join(changed))
    else:
        print(f'{readme}：沒有需要更新的徽章')

# ③ csproj 版本
p = 'XinSpect.csproj'
t = io.open(p, encoding='utf-8').read()
if f'<Version>{NEW_VERSION}</Version>' in t:
    print(f'版本已是 {NEW_VERSION}')
else:
    t = t.replace(f'<Version>{OLD_VERSION}</Version>', f'<Version>{NEW_VERSION}</Version>')
    io.open(p, 'w', encoding='utf-8', newline='').write(t)
    print(f'csproj 版本 {OLD_VERSION} → {NEW_VERSION}')
