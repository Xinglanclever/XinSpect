# -*- coding: utf-8 -*-
"""
dedup_check.py — 曦覽 XinSpect 擴展目錄（ULTIMATE-10000）對倉庫既有事實鍵的查重對帳。

可重跑：所有路徑寫死絕對路徑；報告（AUDIT-dedup.md）中的每個數字都來自本腳本輸出。
用法：python dedup_check.py

比對三級（對「目錄列的候選鍵」逐一判定）：
  (i)   重複     —— 鍵與既有全鍵完全相等（或僅大小寫差）。
  (ii)  疑似收口 —— 既有能力已覆蓋，目錄列至多是補陳述／收口。證據型四種：
        a. 萬用字元前綴：目錄鍵含 {n}/{i}/{k}/{id}/… 或 `*`，其萬用前一段（stem）
           是某既有全鍵的前綴；
        b. 既有鍵為目錄鍵的前綴（段邊界上，R + "." 前綴於 K）；
        c. 動態家族前綴：目錄鍵（或其 stem）落在 FactKeyDynamicCatalog 登記的家族前綴下；
        d. 既有鍵為目錄鍵的延伸（目錄鍵是既有鍵的前綴——既有能力是超集，同樣算既有能力）。
  (iii) 淨新     —— 其餘。
另計：目錄內部自重複鍵（同一鍵出現在多列）——單獨呈報，不併入 (i)。
無鍵列（如 NX 域「不做」宣言）不參與三級判定，單獨計數。
"""
import re
import sys
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding="utf-8")

CATALOG = r"C:\Users\Administrator\.zcode\workspace\draft-0533e841-8f3d-4ca8-8b24-3606a2327a55\ULTIMATE-10000-EXPANSION-2026-10-24.md"
MARKED = r"C:\Users\Administrator\.zcode\workspace\draft-0533e841-8f3d-4ca8-8b24-3606a2327a55\U10-parts\dup-marked.md"
REPO_STATIC = r"C:\Users\Administrator\XinSpect\Services\FactKeyCatalog.cs"
REPO_DYN = r"C:\Users\Administrator\XinSpect\Services\FactKeyDynamicCatalog.cs"

ROW_RE = re.compile(r"^\|\s*(U10-([A-Z]{2})-(\d{4}))\s*\|")
# 鍵形狀：字母/數字開頭；成員為字母、數字、底線、點、{} 萬用/交替段、*、逗號、@、+、連字號。
# （VC 域 dmt.1280x{960,1024}@60、PM 域 kp.pmu.map.westmereep-dp 等都是鍵。）
# GUID 值（0x 開頭，如 GD 域）一律排除。
KEY_RE = re.compile(r"^[a-zA-Z0-9][a-zA-Z0-9_.{}*|,@+-]*$")


def is_key(b):
    return bool(KEY_RE.match(b)) and not b.startswith("0x")
REPO_KEY_RE = re.compile(r'^\s*"([a-z0-9_.]+)",$')
FAM_RE = re.compile(r'^\s*\["([^"]+)"\]')


def load_repo():
    with open(REPO_STATIC, encoding="utf-8") as f:
        keys = [m.group(1) for line in f for m in [REPO_KEY_RE.match(line)] if m]
    with open(REPO_DYN, encoding="utf-8") as f:
        fams = [m.group(1) for line in f for m in [FAM_RE.match(line)] if m]
    return keys, fams


def parse_catalog(path):
    rows, anomalies = [], []
    with open(path, encoding="utf-8") as f:
        for lineno, line in enumerate(f, 1):
            line = line.rstrip("\n")
            m = ROW_RE.match(line)
            if not m:
                continue
            rid, dom = m.group(1), m.group(2)
            rest = line[m.end():]
            blocks = re.findall(r"`([^`]*)`", rest)
            key_blocks = [b for b in blocks if is_key(b)]
            # 非鍵、也非 GUID（0x 開頭）的反引號塊 → 格式異常呈報
            for b in blocks:
                if not is_key(b) and not b.startswith("0x"):
                    anomalies.append((lineno, rid, b))
            if len(key_blocks) > 1:
                anomalies.append((lineno, rid, "多個鍵形塊: " + " / ".join(key_blocks)))
            key = key_blocks[0] if key_blocks else None
            # 列名：ID 欄之後到第一個反引號（或行尾）之間，去掉欄位分隔符
            if "`" in rest:
                name = rest.split("`", 1)[0]
            else:
                name = rest.split("|", 1)[0]
            name = name.strip().rstrip("|").strip()
            rows.append({"id": rid, "dom": dom, "name": name, "key": key, "line": lineno})
    return rows, anomalies


def stem_of(key):
    """萬用字元（{*} 或 {x}）與 `*` 之前的一段。"""
    m = re.match(r"^(.*?)(?:\{[^}]*\}|\*).*$", key)
    return m.group(1) if m else key


def classify(key, repo_lower, fams_lower):
    """回傳 (層級, 證據型, 證據)。層級：DUP / CONV / NEW。"""
    kl = key.lower()
    # (i) 完全相等（僅大小寫差）
    if kl in repo_lower:
        return "DUP", "exact", repo_lower[kl]
    # (ii)c 家族前綴（先於 stem 規則：家族是最直接的既有能力）
    for f in fams_lower:
        if kl.startswith(f):
            return "CONV", "family", f
    st = stem_of(key)
    stl = st.lower().rstrip(".") + "."
    # (ii)a 目錄鍵含萬用字元，stem 前綴命中既有鍵
    if st != key:
        hit = next((r for r in repo_lower if r.startswith(stl)), None)
        if hit:
            return "CONV", "wildcard-stem", hit
    # (ii)b 既有鍵是目錄鍵的前綴（段邊界）
    hit = next((r for r in repo_lower if kl.startswith(r + ".") or kl.startswith(r + "{") or kl.startswith(r + "*")), None)
    if hit:
        return "CONV", "repo-prefix", hit
    # (ii)d 既有鍵是目錄鍵的延伸（目錄鍵為既有鍵前綴——既有能力是超集）
    if "." in kl:
        parent = kl
        while "." in parent:
            parent = parent.rsplit(".", 1)[0]
            if parent in repo_lower:
                return "CONV", "repo-superset", repo_lower[parent]
    return "NEW", None, None


TIER_NAME = {"DUP": "重複", "CONV": "疑似收口", "NEW": "淨新"}


def main():
    repo_keys, fams = load_repo()
    assert len(repo_keys) == len(set(repo_keys)), "repo 鍵有重複"
    repo_lower = {k.lower(): k for k in repo_keys}
    fams_lower = sorted({f.lower() for f in fams})
    rows, anomalies = parse_catalog(CATALOG)

    print("== 輸入基線 ==")
    print(f"倉庫全鍵（FactKeyCatalog.cs）: {len(repo_keys)}")
    print(f"動態家族（FactKeyDynamicCatalog.cs）: {len(fams_lower)}")
    print(f"目錄列總數: {len(rows)}")
    keyed = [r for r in rows if r["key"]]
    nokey = [r for r in rows if not r["key"]]
    print(f"含候選鍵列: {len(keyed)}／無鍵列: {len(nokey)}（無鍵列域分佈: {dict(Counter(r['dom'] for r in nokey))}）")
    ids = Counter(r["id"] for r in rows)
    dup_ids = {k: v for k, v in ids.items() if v > 1}
    print(f"目錄內重複 ID: {len(dup_ids)}" + (f" → {sorted(dup_ids)}" if dup_ids else ""))
    print(f"目錄內自重複鍵（同鍵多列，鍵 >1 列）: ", end="")
    keycnt = Counter(r["key"].lower() for r in keyed)
    intradup = {k: v for k, v in keycnt.items() if v > 1}
    print(f"{len(intradup)} 個鍵，共 {sum(intradup.values())} 列；重複列數（扣除首列）= {sum(v - 1 for v in intradup.values())}")
    print(f"反引號塊格式異常（非鍵形且非 GUID）: {len(anomalies)}")
    for _, rid, b in anomalies:
        print(f"  異常塊 @ {rid}: `{b}`")
    print("（上列異常塊不參與比對）")

    # 三級判定
    for r in keyed:
        tier, etype, ev = classify(r["key"], repo_lower, fams_lower)
        r["tier"], r["etype"], r["ev"] = tier, etype, ev

    per_dom = defaultdict(lambda: {"rows": 0, "keyed": 0, "NEW": 0, "CONV": 0, "DUP": 0})
    for r in rows:
        d = per_dom[r["dom"]]
        d["rows"] += 1
        if r["key"]:
            d["keyed"] += 1
            d[r["tier"]] += 1
    total = Counter(r["tier"] for r in keyed)
    print()
    print("== 總計（僅含鍵列） ==")
    print(f"淨新 {total['NEW']}／疑似收口 {total['CONV']}／重複 {total['DUP']}＝{len(keyed)}")

    print()
    print("== 逐域統計（| 域 | 列數 | 含鍵 | 淨新 | 疑似收口 | 重複 | 無鍵 |）")
    print("| 域 | 列數 | 含鍵 | 淨新 | 疑似收口 | 重複 | 無鍵 |")
    print("|---|---:|---:|---:|---:|---:|---:|")
    for dom in sorted(per_dom, key=lambda d: -per_dom[d]["rows"]):
        d = per_dom[dom]
        print(f"| {dom} | {d['rows']} | {d['keyed']} | {d['NEW']} | {d['CONV']} | {d['DUP']} | {d['rows'] - d['keyed']} |")
    print(f"| 合計 | {len(rows)} | {len(keyed)} | {total['NEW']} | {total['CONV']} | {total['DUP']} | {len(nokey)} |")

    print()
    print("== 疑似收口證據型分佈 ==")
    for et, n in Counter(r["etype"] for r in keyed if r["tier"] == "CONV").most_common():
        print(f"  {et}: {n}")
    print("== 重複證據型分佈 ==")
    for et, n in Counter(r["etype"] for r in keyed if r["tier"] == "DUP").most_common():
        print(f"  {et}: {n}")

    print()
    print("== 目錄內自重複鍵清單（前 25，按重複列數降序） ==")
    for k, v in sorted(intradup.items(), key=lambda x: -x[1])[:25]:
        rid_list = [r["id"] for r in keyed if r["key"].lower() == k]
        print(f"  `{k}` ×{v} → {', '.join(rid_list)}")

    print()
    print("== 疑似收口（CONV）列全清單 ==")
    for r in keyed:
        if r["tier"] == "CONV":
            print(f"  {r['id']}（{r['name']}）→ 疑似收口：`{r['key']}`（{r['etype']}：{r['ev']}）")

    print()
    print("== 重複（DUP）列全清單 ==")
    for r in keyed:
        if r["tier"] == "DUP":
            print(f"  {r['id']}（{r['name']}）→ 重複：`{r['key']}`＝既有鍵 `{r['ev']}`")

    # 79 列標記判定
    marked_rows, _ = parse_catalog(MARKED)
    print()
    print(f"== 標記待查重列（dup-marked.md）：{len(marked_rows)} 列 ==")
    by_id = {r["id"]: r for r in keyed}
    miss = [r["id"] for r in marked_rows if r["id"] not in by_id]
    print(f"未能在主目錄找到對應含鍵列者: {len(miss)}" + (f" → {miss}" if miss else ""))
    mc = Counter()
    for r in marked_rows:
        cat = by_id.get(r["id"])
        if cat is None:
            print(f"  - {r['id']}（{r['name']}）→ 無法判定：主目錄無對應含鍵列")
            continue
        mc[cat["tier"]] += 1
        if cat["tier"] == "NEW":
            print(f"  - {r['id']}（{cat['name']}）→ 淨新：`{cat['key']}`（倉庫無此鍵亦無前綴關係）")
        else:
            print(f"  - {r['id']}（{cat['name']}）→ {TIER_NAME[cat['tier']]}：`{cat['key']}`（{cat['etype']}：{cat['ev']}）")
    print(f"標記 79 列判定分佈：淨新 {mc['NEW']}／疑似收口 {mc['CONV']}／重複 {mc['DUP']}／無法判定 {len(miss)}")

    # 輔助證據（不影響三級判定）：對標記列列出倉庫「近鄰鍵」——
    # 與目錄鍵之最長共同字元前綴 ≥3 且落在點邊界上（即至少共用首段命名空間）。
    # 用途：標記註記常宣稱「查重：X 已有」，近鄰輸出讓該宣稱可對到倉庫實際鍵名。
    print()
    print("== 標記列近鄰證據（輔助；共同前綴 ≥3 字元且止於點邊界；前 3 筆） ==")

    def lcp(a, b):
        n = min(len(a), len(b))
        i = 0
        while i < n and a[i] == b[i]:
            i += 1
        return a[:i]

    def neighbors(kl):
        out = []
        for r in repo_lower:
            p = lcp(kl, r)
            if len(p) >= 3 and p.endswith("."):
                out.append((len(p), r))
        for f in fams_lower:
            p = lcp(kl, f)
            if len(p) >= 3 and p.endswith("."):
                out.append((len(p), f + "（家族）"))
        out.sort(key=lambda x: -x[0])
        return [e for _, e in out[:3]]

    for r in marked_rows:
        cat = by_id.get(r["id"])
        if cat is None or not cat["key"]:
            continue
        nb = neighbors(cat["key"].lower())
        if nb:
            print(f"  {r['id']} `{cat['key']}` 近鄰: {', '.join(nb)}")

    print()
    print("== 無鍵列清單 ==")
    for r in nokey:
        print(f"  {r['id']}（{r['name']}）→ 無候選鍵（不參與三級判定）")


if __name__ == "__main__":
    main()
