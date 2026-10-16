# -*- coding: utf-8 -*-
"""盤點這一階段新增的服務：哪些被生產碼引用、哪些只有測試引用。

這就是「接線守門」要看的那件事，但用人工盤點一次，確認守門沒有漏掉什麼。
"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

NEW_SERVICES = [
    'ScanSkeleton', 'ScanStepTimeout', 'TransientScope', 'SourceAvailability',
    'SourceRegistry', 'SourceChain', 'OfflineInputMount', 'SourceCapabilityMatrix',
    'ScanSkeletonReport', 'FactStateReport', 'SpecRefReport', 'KnowledgePackReport',
    'KnowledgeLookupReport', 'SensitivityPolicy', 'DomainKnowledgeReport', 'EgressPolicy',
    'GuardPolicy', 'GuardRegistry', 'GuardBudget', 'DataSovereigntyReport',
    'WriteGateReport', 'AuditExportReport', 'KeyCatalogPolicy', 'ResilienceReport',
    'ReleaseReport', 'FactCoverageReport',
]

roots = ['Services', 'Tests', 'Views', 'ViewModels', 'Controls', 'Nav', 'Models']
files = []
for r in roots:
    if not os.path.isdir(r):
        continue
    for f in os.listdir(r):
        if f.endswith('.cs'):
            files.append(os.path.join(r, f))

print(f'掃描 {len(files)} 個 .cs 檔\n')
only_tests = []
for svc in NEW_SERVICES:
    prod = test = 0
    for path in files:
        if os.path.basename(path) == f'{svc}.cs':
            continue
        try:
            t = io.open(path, encoding='utf-8').read()
        except Exception:
            continue
        n = len(re.findall(r'\b' + re.escape(svc) + r'\b', t))
        if n == 0:
            continue
        if path.startswith('Tests'):
            test += n
        else:
            prod += n
    flag = '' if prod > 0 else '   ← 只有測試引用！'
    print(f'{svc:26s} 生產碼 {prod:3d} 處／測試 {test:3d} 處{flag}')
    if prod == 0:
        only_tests.append(svc)

print()
if only_tests:
    print(f'⚠ 只有測試引用的服務（{len(only_tests)} 支）：' + '、'.join(only_tests))
    print('  這些就是「寫好了、測過了、但沒有接進產品」的候選——')
    print('  它們的事實鍵不會出現在任何使用者的報告裡。')
else:
    print('全部服務都有生產碼引用。')
