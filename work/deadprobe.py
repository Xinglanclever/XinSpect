"""找出「探針預設回常數、且生產入口從不傳探針」的死路（fw.fpdt 那一類）。

做法：
1. 掃 Services/*.cs，找 `XxxProbe? name = null` 形式的可選探針參數；
2. 看同一檔案裡這個探針的使用是否長成 `(probe ?? (() => 常數))()` 或
   `probe ?? (() => null)`——即預設值是一個不回真資料的 lambda；
3. 再看這個 Collect/Fact 方法在 EvidenceLabService.cs 裡的呼叫點有沒有傳探針；
   沒有傳＝生產路徑永遠走預設 → 死路。
"""
import io, re, glob, os

root = r'C:\Users\Administrator\XinSpect'
svc_dir = os.path.join(root, 'Services')
lab = io.open(os.path.join(svc_dir, 'EvidenceLabService.cs'), encoding='utf-8').read()

# 1) 每個服務裡的可選探針參數
dead = []
for path in glob.glob(os.path.join(svc_dir, '*.cs')):
    name = os.path.basename(path)
    if name in ('EvidenceLabService.cs', 'EvidenceCollection.cs'):
        continue
    try:
        text = io.open(path, encoding='utf-8').read()
    except Exception:
        continue

    # (a) 形如 `FooProbe? foo = null` 的參數
    for m in re.finditer(r'(\w+Probe)\?\s+(\w+)\s*=\s*null', text):
        probe_type, param = m.group(1), m.group(2)

        # (b) 預設值是不是常數 lambda（不回真資料）
        const_default = re.search(
            re.escape(param) + r'\s*\?\?\s*\(\(\s*\)\s*=>\s*(null|default|\(0|0\b|\(\s*0)',
            text)
        const_default2 = re.search(
            re.escape(param) + r'\s*\?\?\s*new\s+' + re.escape(probe_type) + r'\(', text)
        if not (const_default or const_default2):
            continue

        # (c) 這個 Collect 方法有沒有被生產入口呼叫（且傳了探針）
        #     先把方法名找出來：以該檔案裡出現的 public static 方法為準
        methods = re.findall(r'public static [^\n(]*?(\w+)\s*\(', text)
        called = [mm for mm in methods if mm + '(' in lab]
        if not called:
            # 生產入口根本沒呼叫這個服務 → 由 orphan gate 負責，不在本檢查範圍
            continue

        # 有呼叫，但呼叫點有沒有帶 named argument（探針）？
        passes_probe = False
        for mm in called:
            for call in re.finditer(re.escape(mm) + r'\s*\((.*?)\)', lab, re.S):
                args = call.group(1)
                if param in args or 'Probe' in args:
                    passes_probe = True
                    break
            if passes_probe:
                break

        if not passes_probe:
            dead.append((name, probe_type, param, ','.join(called[:2])))

print(f'{"file":46} {"probe":26} {"param":22} caller')
for f, t, p, c in sorted(dead):
    print(f'{f:46} {t:26} {p:22} {c}')
print('total suspects:', len(dead))
