#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
U10 四方查重腳本（自動初判）。

對帳對象：
  ① 新目錄自己的候選鍵（ULTIMATE-10000-EXPANSION-2026-10-24.md 的 U10 表格列）
  ② 主程式事實鍵目錄 Services/FactKeyCatalog.cs 與 Services/FactKeyDynamicCatalog.cs
  ③ Services/ 與 Models/ 所有 .cs 原始碼裡出現的鍵形字串字面
  ④ 既有三份計畫目錄（docs/INDUSTRIAL-CATALOG-2026-10-10.md、
     docs/INDUSTRIAL-CATALOG-VOL2-2026-10-11.md、docs/ULTIMATE-1000/*.md）

輸出：
  work/u10_dedup/report.md   （中文初判報告，開頭附「初判不等於結論」聲明）
  work/u10_dedup/report.json （機器可讀，ensure_ascii=True）

可重跑（冪等）：只讀倉庫其他檔案，只寫 work/u10_dedup/ 下兩個輸出檔。
路徑：所有輸入路徑都相對「倉庫根」解析；倉庫根 = 本腳本上兩層目錄。
只依賴 Python 3 標準庫。
"""

import json
import os
import re
import sys
from collections import defaultdict

# ---------------------------------------------------------------------------
# 路徑設定（相對倉庫根；repo root = work/u10_dedup/ 的上兩層）
# ---------------------------------------------------------------------------
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))

NEW_CATALOG = os.path.join(
    os.path.expanduser("~"),
    ".zcode", "workspace", "draft-0533e841-8f3d-4ca8-8b24-3606a2327a55",
    "ULTIMATE-10000-EXPANSION-2026-10-24.md",
)
FACTKEY_CATALOG = os.path.join(REPO, "Services", "FactKeyCatalog.cs")
FACTKEY_DYNAMIC = os.path.join(REPO, "Services", "FactKeyDynamicCatalog.cs")
SRC_DIRS = [os.path.join(REPO, "Services"), os.path.join(REPO, "Models")]
OLD_CATALOG_FILES = [os.path.join(REPO, "docs", "INDUSTRIAL-CATALOG-2026-10-10.md"),
                     os.path.join(REPO, "docs", "INDUSTRIAL-CATALOG-VOL2-2026-10-11.md")]
OLD_CATALOG_DIRS = [os.path.join(REPO, "docs", "ULTIMATE-1000")]

REPORT_MD = os.path.join(HERE, "report.md")
REPORT_JSON = os.path.join(HERE, "report.json")

# ---------------------------------------------------------------------------
# 鍵的形狀
# ---------------------------------------------------------------------------
# 具體鍵（出現在 .cs 字串字面或 md 反引號裡、可逐字比對）：
#   每段為字母開頭或數字開頭的 [A-Za-z0-9_-]，以點分段，至少兩段。
#   首段必須以字母開頭，避免把版本號 "1.0.0.0"、IP、hex 之類誤收。
#   .cs 掃描再加小寫首段限制：本專案事實鍵首段全為小寫（駝峰的
#   "BootBreakdown.Refresh"、"XinSpect.Trace" 之類是方法名／事件名，不是鍵）。
RE_CONCRETE_KEY = re.compile(r"^[A-Za-z][A-Za-z0-9_\-]*(?:\.[A-Za-z0-9_\-]+)+$")
RE_LOWER_KEY = re.compile(r"^[a-z][a-z0-9_\-]*(?:\.[A-Za-z0-9_\-]+)+$")

# 明顯是檔名而非鍵的字串（末段是副檔名）：advapi32.dll、cmd.exe、audit.json 之類。
FILE_EXTS = {
    "dll", "exe", "sys", "json", "jsonl", "log", "txt", "xml", "png", "jpg",
    "jpeg", "mp3", "wav", "p7b", "bin", "cs", "xaml", "py", "cpl", "msc",
    "tlb", "nls", "cat", "inf", "pdb", "config", "cache", "dat", "db", "vhd",
    "vhdx", "iso", "zip", "evtx", "ps1", "cmd", "bat", "md", "csv", "h", "lib",
}


def is_file_name_like(s):
    return s.rsplit(".", 1)[-1].lower() in FILE_EXTS

# 候選鍵（新目錄反引號裡）額外允許佔位記號：{...}、*、@（如 amd.pstate.{ctl,status}、
# fan{i}.rpm、dmt.640x350@85）——這些不能做逐字比對，但可以進家族判定。
RE_CAND_KEY = re.compile(
    r"^[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_{}*@\-]+)+$"
)

# 從 .cs 抽引號內字串（不含跳脫與換行）
RE_CS_STR = re.compile(r'"([^"\\\n]{2,120})"')
# 從 md 抽反引號內字串
RE_MD_BT = re.compile(r"`([^`\n]{1,120})`")
# U10 表格列：| U10-XX-NNNN | 描述 | ... |
RE_U10_ROW = re.compile(r"^\|\s*(U10-[A-Z]{2}-\d{4})\s*\|(.*)$")
# U10 ID 中的域碼
RE_DOMAIN = re.compile(r"^U10-([A-Z]{2})-")
# FactKeyDynamicCatalog.cs 的家族前綴宣告：["pci.dev."] = "...",
RE_DYN_PREFIX = re.compile(r'\[\s*"([^"]+)"\s*\]\s*=')


def split_cells(line):
    """把 markdown 表格列切成儲存格（尊重 \\| 轉義）。"""
    body = line.strip()
    if body.startswith("|"):
        body = body[1:]
    if body.endswith("|"):
        body = body[:-1]
    return [c.strip().replace("\\|", "|") for c in re.split(r"(?<!\\)\|", body)]


def read_text(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        return f.read()


def family_of(key):
    """家族＝前兩段。鍵不足兩段時回 None。"""
    parts = key.split(".")
    if len(parts) >= 2:
        return parts[0] + "." + parts[1]
    return None


# ---------------------------------------------------------------------------
# ① 新目錄：抽 U10 條目
# ---------------------------------------------------------------------------
def parse_u10_entries(path):
    """回 (entries, lines_total)。entry = dict(id, desc, keys, raw, line_no)。

    鍵的判定：儲存格（第 3 欄以後，即鍵欄與其後）反引號內、能匹配 RE_CAND_KEY
    的字串。反引號裡的 GUID、hex 序列、無點字串一律不算鍵
    （誠實原則：抓不到就標無鍵，不硬湊）。
    """
    text = read_text(path)
    lines = text.splitlines()
    entries = []
    for i, line in enumerate(lines, 1):
        m = RE_U10_ROW.match(line)
        if not m:
            continue
        uid = m.group(1)
        cells = split_cells(line)
        # cells[0] = ID；cells[1] = 描述；cells[2:] = 鍵欄及其後
        desc = cells[1] if len(cells) > 1 else ""
        key_area = " ".join(cells[2:]) if len(cells) > 2 else ""
        keys = []
        for tok in RE_MD_BT.findall(key_area):
            tok = tok.strip()
            if RE_CAND_KEY.match(tok):
                keys.append(tok)
        entries.append({
            "id": uid,
            "desc": desc,
            "keys": keys,
            "raw": line,
            "line_no": i,
        })
    return entries, len(lines)


# ---------------------------------------------------------------------------
# ②③④ 既有鍵集合
# ---------------------------------------------------------------------------
def collect_cs_keys(path, source_label, keys_by_src, authoritative=False):
    """掃單一 .cs 檔，抽引號內具體鍵形字串。

    authoritative=True（事實鍵目錄本身）：只要符合具體鍵形就收。
    否則（一般原始碼）：再加小寫首段限制與副檔名排除，壓掉方法名與檔名雜訊。
    """
    text = read_text(path)
    for s in RE_CS_STR.findall(text):
        if RE_CONCRETE_KEY.match(s):
            if not authoritative and not (RE_LOWER_KEY.match(s) and not is_file_name_like(s)):
                continue
            keys_by_src.setdefault(s, set()).add(source_label)


def collect_dynamic_prefixes(path, prefix_families):
    """從 FactKeyDynamicCatalog.cs 抽 ["xxx."] = 的家族前綴宣告。"""
    text = read_text(path)
    rel = os.path.relpath(path, REPO).replace("\\", "/")
    for tok in RE_DYN_PREFIX.findall(text):
        base = tok.rstrip(".")
        parts = [p for p in base.split(".") if p]
        if len(parts) >= 2:
            prefix_families[parts[0] + "." + parts[1]].add(rel)


def collect_source_keys(keys_by_src):
    for d in SRC_DIRS:
        for root, _, files in os.walk(d):
            for fn in sorted(files):
                if not fn.endswith(".cs"):
                    continue
                full = os.path.join(root, fn)
                rel = os.path.relpath(full, REPO).replace("\\", "/")
                # FactKeyCatalog.cs / FactKeyDynamicCatalog.cs 會在下面單獨標記，
                # 這裡先照常收（rel 路徑本身就是檔案標記）。
                collect_cs_keys(full, rel, keys_by_src, None)


def collect_md_keys(path, keys_by_src, prefix_families):
    """掃既有目錄 md：反引號內的鍵。

    具體鍵（RE_CONCRETE_KEY）進 keys_by_src；
    帶萬用/佔位/前綴形（*、{...}、以 . 結尾、或段含 {}）進 prefix_families——
    它們是家族申報（如 ac.cpc.*、tpm.replay.pcr.{alg}.{i}、pci.dev.）。
    """
    text = read_text(path)
    rel = os.path.relpath(path, REPO).replace("\\", "/")
    for tok in RE_MD_BT.findall(text):
        tok = tok.strip()
        if RE_CONCRETE_KEY.match(tok):
            keys_by_src.setdefault(tok, set()).add(rel)
        elif re.match(r"^[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_{}*@\-]+)+\.?$", tok):
            # 家族形：去佔位後取前兩段
            base = tok.rstrip(".")
            parts = [p for p in base.split(".") if p and not re.search(r"[{}*]", p)]
            if len(parts) >= 2:
                prefix_families[parts[0] + "." + parts[1]].add(rel)


def existing_key_label(key, keys_by_src):
    """回這把既有鍵出現的檔案清單（用於 key-dup 的「撞在哪個檔案」）。"""
    return sorted(keys_by_src.get(key, ()))


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
def main():
    problems = []
    if not os.path.isfile(NEW_CATALOG):
        problems.append("找不到新目錄檔：" + NEW_CATALOG)
    for p in [FACTKEY_CATALOG, FACTKEY_DYNAMIC] + OLD_CATALOG_FILES:
        if not os.path.isfile(p):
            problems.append("找不到輸入檔：" + p)
    for d in SRC_DIRS + OLD_CATALOG_DIRS:
        if not os.path.isdir(d):
            problems.append("找不到輸入目錄：" + d)
    if problems:
        for p in problems:
            print("[錯誤]", p, file=sys.stderr)
        sys.exit(1)

    # ① 新目錄
    entries, new_total_lines = parse_u10_entries(NEW_CATALOG)
    domains = defaultdict(int)
    for e in entries:
        m = RE_DOMAIN.match(e["id"])
        if m:
            domains[m.group(1)] += 1
    with_key = [e for e in entries if e["keys"]]
    no_key = [e for e in entries if not e["keys"]]

    # ②③ 事實鍵目錄 + 原始碼
    keys_by_src = {}  # key -> set(檔案標記)
    collect_cs_keys(FACTKEY_CATALOG, "Services/FactKeyCatalog.cs", keys_by_src,
                    authoritative=True)
    collect_cs_keys(FACTKEY_DYNAMIC, "Services/FactKeyDynamicCatalog.cs", keys_by_src,
                    authoritative=True)
    collect_source_keys(keys_by_src)

    # 家族前綴申報：既有目錄 md（*／{...}／前綴形）＋ FactKeyDynamicCatalog.cs
    prefix_families = defaultdict(set)  # 家族 -> set(來源檔)
    collect_dynamic_prefixes(FACTKEY_DYNAMIC, prefix_families)

    # 既有目錄 md
    old_md_files = list(OLD_CATALOG_FILES)
    for d in OLD_CATALOG_DIRS:
        old_md_files += [os.path.join(d, fn) for fn in sorted(os.listdir(d))
                         if fn.endswith(".md")]
    for p in old_md_files:
        collect_md_keys(p, keys_by_src, prefix_families)

    # 家族索引：家族 -> 既有鍵集合；家族 -> 來源檔集合
    fam_keys = defaultdict(set)
    for k in keys_by_src:
        fam = family_of(k)
        if fam:
            fam_keys[fam].add(k)
    fam_srcs = defaultdict(set)
    for k, srcs in keys_by_src.items():
        fam = family_of(k)
        if fam:
            fam_srcs[fam] |= srcs

    # ④ 三級判定（家族＝前兩段；命中「具體鍵家族」或「前綴申報家族」都算家族重疊）
    def fam_hit(fam):
        return fam in fam_keys or fam in prefix_families

    key_dup, fam_overlap, unique = [], [], []
    for e in entries:
        dups = []       # (鍵, 撞在哪些檔)
        fams = []       # (家族, 家族內 U10 鍵)
        for k in e["keys"]:
            if k in keys_by_src:
                dups.append((k, existing_key_label(k, keys_by_src)))
            fam = family_of(k)
            if fam and fam_hit(fam):
                fams.append((fam, k))
        if dups:
            key_dup.append({**e, "dups": dups, "fams": fams})
        elif fams:
            fam_overlap.append({**e, "fams": fams, "dups": dups})
        elif e["keys"]:
            unique.append(e)
        else:
            # 無鍵條目：沒有鍵可比對，單獨歸類（不硬塞進 unique）
            e2 = dict(e)
            e2["nokey"] = True
            unique.append(e2)

    fam_overlap_by_family = defaultdict(list)
    for e in fam_overlap:
        for fam in sorted({f for f, _ in e["fams"]}):
            fam_overlap_by_family[fam].append(e["id"])

    # 〔查重〕標記條目：描述或整列出現「查重」字樣的 U10 表格列
    #（目錄正文自稱 84 處；實際表格列內含「查重」的筆數以腳本數出來的為準，兩者都寫。）
    chazhong = [e for e in entries if "查重" in e["raw"]]

    # 描述欄裡也出現鍵形反引號字串的條目（這些字串刻意不計入候選鍵，避免把
    # 「沿用既有鍵」的敘述誤判成撞鍵；數量如實申報）
    desc_bt = [e for e in entries
               if any(RE_CAND_KEY.match(t.strip())
                      for t in RE_MD_BT.findall(e["desc"]))]

    # -----------------------------------------------------------------
    # report.json
    # -----------------------------------------------------------------
    report = {
        "generated": "自動查重初判（dedup.py）",
        "inputs": {
            "new_catalog": NEW_CATALOG,
            "factkey_catalog": "Services/FactKeyCatalog.cs",
            "factkey_dynamic": "Services/FactKeyDynamicCatalog.cs",
            "source_dirs": ["Services/", "Models/"],
            "old_catalog_files": [os.path.relpath(p, REPO).replace("\\", "/")
                                  for p in old_md_files],
        },
        "stats": {
            "new_catalog_lines": new_total_lines,
            "u10_entries": len(entries),
            "domains": len(domains),
            "domain_counts": dict(sorted(domains.items())),
            "entries_with_keys": len(with_key),
            "entries_without_keys": len(no_key),
            "distinct_candidate_keys": len({k for e in entries for k in e["keys"]}),
            "existing_concrete_keys": len(keys_by_src),
            "existing_concrete_keys_from_cs": len({k for k, s in keys_by_src.items()
                                                   if any(x.endswith(".cs") for x in s)}),
            "existing_family_declarations_from_md": len(prefix_families),
            "existing_families_two_segment": len(fam_keys),
            "key_dup": len(key_dup),
            "family_overlap": len(fam_overlap),
            "family_overlap_families": len(fam_overlap_by_family),
            "unique_with_keys": len([e for e in unique if not e.get("nokey")]),
            "no_key": len(no_key),
            "chazhong_marked_rows": len(chazhong),
            "desc_cell_contains_keylike_backticks": len(desc_bt),
        },
        "key_dup": [
            {"id": e["id"], "desc": e["desc"],
             "dup_keys": [{"key": k, "found_in": files} for k, files in e["dups"]]}
            for e in key_dup
        ],
        "family_overlap": [
            {"id": e["id"], "desc": e["desc"], "keys": e["keys"],
             "families": sorted({f for f, _ in e["fams"]})}
            for e in fam_overlap
        ],
        "family_overlap_by_family": {
            fam: {"existing_count": len(fam_keys[fam]),
                  "sample_existing": sorted(fam_keys[fam])[:8],
                  "prefix_declared_in": sorted(prefix_families.get(fam, ())),
                  "u10_ids": list(ids)}
            for fam, ids in sorted(fam_overlap_by_family.items())
        },
        "unique": [{"id": e["id"], "keys": e["keys"], "desc": e["desc"]}
                   for e in unique if not e.get("nokey")],
        "no_key_entries": [{"id": e["id"], "desc": e["desc"]} for e in unique
                           if e.get("nokey")],
        "chazhong": [{"id": e["id"], "desc": e["desc"]} for e in chazhong],
        "desc_bt_ids": [e["id"] for e in desc_bt],
    }
    with open(REPORT_JSON, "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=True, indent=1)

    # -----------------------------------------------------------------
    # report.md
    # -----------------------------------------------------------------
    L = []
    ap = L.append
    ap("# U10 四方查重・自動初判報告")
    ap("")
    ap("**這是自動查重的初判。`key-dup` 與 `family-overlap` 不等於重複**：")
    ap("")
    ap("- 撞鍵可能是**既有實作已收口**——該列應標「收口」，引用既有鍵即可；")
    ap("- 也可能是**候選鍵命名撞車**——改一個名就是新列，不必刪；")
    ap("- 家族重疊只說明「前兩段同家族」，同家族不等於同能力。")
    ap("")
    ap("**結論以人工複核為準**，本報告只負責把嫌疑名單列全。")
    ap("")
    ap("---")
    ap("")
    ap("## 一、總量統計")
    ap("")
    st = report["stats"]
    ap(f"- 新目錄檔：`{os.path.basename(NEW_CATALOG)}`，共 {new_total_lines} 行")
    ap(f"- U10 條目總數：**{st['u10_entries']}**（不重複 ID {len({e['id'] for e in entries})}）")
    ap(f"- 域數：**{st['domains']}**（U10-XX 的 XX）")
    dom_line = "、".join(f"{d}({n})" for d, n in sorted(domains.items()))
    ap(f"  - 各域條數：{dom_line}")
    ap(f"- 有鍵條目：**{st['entries_with_keys']}**；無鍵條目：**{st['entries_without_keys']}**"
       f"（無鍵＝該列反引號內抽不出符合鍵形狀的字串）")
    ap(f"- 候選鍵去重後：{st['distinct_candidate_keys']} 把")
    ap(f"- 既有具體鍵（.cs 字面 + 既有目錄 md 反引號，合併去重）：**{st['existing_concrete_keys']}** 把"
       f"（其中出自 .cs 的 {st['existing_concrete_keys_from_cs']} 把）")
    ap(f"- 既有目錄 md 裡的家族形申報（`*`／`{{...}}`／前綴）：{st['existing_family_declarations_from_md']} 個家族")
    ap(f"- 既有具體鍵構成的兩段家族數：{st['existing_families_two_segment']}")
    ap("")
    ap("## 二、key-dup（候選鍵與既有鍵逐字相同）")
    ap("")
    ap(f"共 **{len(key_dup)}** 筆。")
    ap("")
    if key_dup:
        ap("| U10 ID | 鍵 | 描述 | 撞在哪個檔案 |")
        ap("|---|---|---|---|")
        for e in key_dup:
            for k, files in e["dups"]:
                desc = e["desc"].replace("|", "\\|")
                srcs = "、".join(f"`{s}`" for s in files) if files else "（來源標記缺失）"
                ap(f"| {e['id']} | `{k}` | {desc} | {srcs} |")
    else:
        ap("（無）")
    ap("")
    ap("## 三、family-overlap（前兩段家族與既有鍵重疊）")
    ap("")
    ap(f"共 **{len(fam_overlap)}** 筆條目，涉及 **{len(fam_overlap_by_family)}** 個家族。")
    ap("")
    ap("| 家族 | 既有鍵數 | 既有鍵抽樣 | U10 條目（抽樣） | U10 條目數 |")
    ap("|---|---:|---|---|---:|")
    for fam in sorted(fam_overlap_by_family):
        ids = fam_overlap_by_family[fam]
        ex = sorted(fam_keys[fam])
        pref = sorted(prefix_families.get(fam, ()))
        u10_keys_in_fam = sorted({k for e in fam_overlap if fam in {f for f, _ in e["fams"]}
                                  for k in e["keys"] if family_of(k) == fam})
        sample = "、".join(f"`{k}`" for k in ex[:5]) + ("…" if len(ex) > 5 else "")
        if not ex and pref:
            sample = "（僅前綴申報：" + "、".join(f"`{p}`" for p in pref[:3]) + "）"
        sample_u = "、".join(f"`{k}`" for k in u10_keys_in_fam[:5]) + ("…" if len(u10_keys_in_fam) > 5 else "")
        ap(f"| `{fam}.` | {len(ex)} | {sample} | {sample_u} | {len(ids)} |")
    ap("")
    ap("### 家族對照抽樣（每家族最多 6 筆）")
    ap("")
    shown = 0
    for fam in sorted(fam_overlap_by_family):
        ids = fam_overlap_by_family[fam]
        ex = sorted(fam_keys[fam])
        pref = sorted(prefix_families.get(fam, ()))
        u10_keys_in_fam = sorted({k for e in fam_overlap if fam in {f for f, _ in e["fams"]}
                                  for k in e["keys"] if family_of(k) == fam})
        head = f"- **`{fam}.`**（既有 {len(ex)} 鍵 / U10 {len(ids)} 筆）"
        if ex:
            head += "：既有如 " + "、".join(f"`{k}`" for k in ex[:3])
        elif pref:
            head += "：僅前綴申報（" + "、".join(f"`{p}`" for p in pref[:3]) + "），無具體鍵可比"
        if u10_keys_in_fam:
            head += "；U10 如 " + "、".join(f"`{k}`" for k in u10_keys_in_fam[:3])
        ap(head)
        shown += 1
        if shown >= 60:
            ap(f"- …（其餘 {len(fam_overlap_by_family) - shown} 個家族見 report.json 的 family_overlap_by_family）")
            break
    ap("")
    ap("## 四、unique（無撞鍵）")
    ap("")
    ap(f"共 **{len([e for e in unique if not e.get('nokey')])}** 筆有鍵條目在三方既有集合中都找不到同鍵、"
       f"也找不到同兩段家族。另有 **{len(no_key)}** 筆**無鍵條目**——它們沒有鍵可比，"
       f"本腳本無法替它們判定撞鍵與否，清單見 report.json 的 `no_key_entries`，需人工補鍵或人工判定。")
    ap("")
    ap("完整清單見 `report.json` 的 `unique` 欄。")
    ap("")
    ap("## 五、〔查重〕標記條目（供下一輪人工收口）")
    ap("")
    ap(f"目錄正文自稱「〔查重〕84 處」；本腳本在 U10 表格列內數到含「查重」字樣的列 **{len(chazhong)}** 列"
       f"（含 〔查重〕〔查重——〔查重：等變體，純正文敘述行不計）。兩個數字對不上就以本腳本清單為準逐列核。")
    ap("")
    ap("| U10 ID | 描述 |")
    ap("|---|---|")
    for e in chazhong:
        desc = e["desc"].replace("|", "\\|")
        ap(f"| {e['id']} | {desc} |")
    ap("")
    ap("## 六、方法與限制（誠實聲明）")
    ap("")
    ap("- 候選鍵：U10 表格列「鍵欄及其後」反引號內、形如 `a.b…` 的字串；GUID、hex 序列、"
       "無點字串不算鍵。描述欄內出現的鍵形反引號字串刻意不計（避免把「沿用既有鍵」的敘述誤判成撞鍵）；"
       f"這種條目有 {len(desc_bt)} 筆，清單見 report.json `stats` 之計數與 `desc_bt_ids`。")
    ap("- 既有鍵：.cs 引號內具體鍵形字串（含註解裡出現的），加上既有目錄 md 反引號內的具體鍵。")
    ap("  一般原始碼的掃描另加兩道雜訊過濾：首段必須全小寫（駝峰串是方法名／事件名不是鍵）、"
       "末段是常見副檔名的排除（advapi32.dll、cmd.exe 是檔名不是鍵）。")
    ap("  這個抽法仍可能混入少量非鍵的字串字面（誤報方向），也可能漏掉拼出來的動態鍵（漏報方向）——")
    ap("  `FactKeyDynamicCatalog` 已登記的動態家族前綴已併入家族判定。")
    ap("- 家族＝鍵的前兩段。家族重疊是**粗篩**，同家族下是否同一能力必須人工看描述。")
    ap("- 無鍵條目與查重標記條目的數目如實列出，不硬湊。")
    ap("")

    with open(REPORT_MD, "w", encoding="utf-8") as f:
        f.write("\n".join(L))

    # 主控台摘要
    print("== U10 四方查重初判 ==")
    print(f"U10 條目：{st['u10_entries']}（域 {st['domains']}；有鍵 {st['entries_with_keys']}／無鍵 {st['entries_without_keys']}）")
    print(f"key-dup：{len(key_dup)} 筆")
    print(f"family-overlap：{len(fam_overlap)} 筆 / {len(fam_overlap_by_family)} 個家族")
    print(f"unique（有鍵無撞）：{len([e for e in unique if not e.get('nokey')])} 筆")
    print(f"無鍵：{len(no_key)} 筆；查重標記列：{len(chazhong)} 列")
    print(f"報告：{REPORT_MD}")
    print(f"JSON：{REPORT_JSON}")


if __name__ == "__main__":
    main()
