#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""README 逐域展開的產生器（第 282 輪）。

README 的第 6.3 節（逐域鍵表）、第 6.5 節（逐鍵出處）與附錄 F（域前綴表）
是把事實鍵目錄**整份展開**——149 個域、1,128 把鍵。那份內容原本由一次性腳本寫成，
改完目錄沒重跑就與原始碼脫節，而且沒有任何東西會叫（第 279 輪補了結構守門，
但守門只驗「有沒有漏或多」，不會幫你把漏的補上）。

這支工具做兩件事：

  python Tools/gen-readme-domains.py            # 就地重生（只動這三個區段）
  python Tools/gen-readme-domains.py --check    # 只比對，不寫檔；有差異 exit 2

**只動它負責的三個區段**，其餘一個字都不碰。已有的列／條目**原樣保留**
（名稱、來源、可信度、註解、原始碼片段都是人工與工具協作的結果，重跑不該洗掉），
只有目錄裡新出現、文件裡還沒有的鍵才由原始碼抽後設資料補上——
抽不到就留「—」，那是如實的「這個抽取方式抓不到」，不是遺漏。

用法前提：在倉庫根執行（或任意位置，腳本會自己找根）。
"""

import argparse
import os
import re
import sys

# ── 常數：這支工具負責的三個區段 ─────────────────────────────────────────
SEC_63_START = "### 6.3 逐域鍵表"
SEC_63_END = "### 6.4 跨域通則"
SEC_65_START = "### 6.5 逐鍵出處"
SEC_65_END = "## 第 7 部"
APP_F_START = "### 附錄 F　"
APP_F_END = "### 附錄 F-2"

CATALOG = os.path.join("Services", "FactKeyCatalog.cs")
FAMILIES = os.path.join("Services", "FactKeyDynamicCatalog.cs")
SERVICES = "Services"
README = "README.md"


def repo_root():
    d = os.path.dirname(os.path.abspath(__file__))
    while d and not os.path.exists(os.path.join(d, "XinSpect.csproj")):
        parent = os.path.dirname(d)
        if parent == d:
            break
        d = parent
    if not os.path.exists(os.path.join(d, "XinSpect.csproj")):
        sys.exit("找不到倉庫根（往上找 XinSpect.csproj 失敗）")
    return d


def read(path):
    with open(path, encoding="utf-8-sig", newline="") as f:
        return f.read()


def write(path, text):
    with open(path, "w", encoding="utf-8-sig" if path.endswith(".cs") else "utf-8",
              newline="") as f:
        f.write(text)


# ── 讀目錄 ───────────────────────────────────────────────────────────────

def load_keys(root):
    text = read(os.path.join(root, CATALOG))
    keys = re.findall(r'^\s{8}"([^"]+)",\s*$', text, re.M)
    if not keys:
        sys.exit("事實鍵目錄解析出 0 把鍵——格式變了，先修這支工具")
    if len(keys) != len(set(keys)):
        dup = sorted({k for k in keys if keys.count(k) > 1})
        sys.exit("目錄有重複鍵：" + "、".join(dup[:10]))
    return keys


def group_domains(keys):
    dom = {}
    for k in keys:
        dom.setdefault(k.split(".")[0], []).append(k)
    for d in dom:
        dom[d].sort()
    # 排序規則：鍵數遞減，同數按域名字典序（README 一貫的排法，第 279 輪守門釘住）
    return sorted(dom.items(), key=lambda kv: (-len(kv[1]), kv[0]))


def load_families(root):
    text = read(os.path.join(root, FAMILIES))
    return re.findall(r'^\s{12}\["([^"]+)"\] =', text, re.M)


# ── 從原始碼抽後設資料（只給「文件裡還沒有的鍵」用） ──────────────────────

def source_corpus(root):
    out = []
    base = os.path.join(root, SERVICES)
    for dirpath, _, files in os.walk(base):
        for name in files:
            if name.endswith(".cs"):
                p = os.path.join(dirpath, name)
                out.append((os.path.relpath(p, root).replace(os.sep, "/"), read(p)))
    return out


def extract_meta(corpus, key):
    """回 (名稱, 來源, 可信度)。抽不到就回 ('—','—','—')。"""
    pat = re.compile(
        r'new\s+HardwareFact\(\s*"' + re.escape(key) + r'"\s*,\s*[^,]+,\s*"([^"]*)"\s*,'
        r'\s*[^,]+?,\s*[^,]+?,\s*"([^"]*)"\s*,\s*FactTrustLevel\.(\w+)')
    for _, text in corpus:
        m = pat.search(text)
        if m:
            return (m.group(1), m.group(2), m.group(3))
    pat2 = re.compile(
        r'new\(\s*"' + re.escape(key) + r'"\s*,\s*[^,]+,\s*"([^"]*)"\s*,'
        r'\s*[^,]+?,\s*[^,]+?,\s*"([^"]*)"\s*,\s*FactTrustLevel\.(\w+)')
    for _, text in corpus:
        m = pat2.search(text)
        if m:
            return (m.group(1), m.group(2), m.group(3))
    return ("—", "—", "—")


def extract_bullet(corpus, key):
    """回 6.5 的一條項目（多行字串）。抽不到回一句如實的說明。"""
    lit = '"' + key + '"'
    for rel, text in corpus:
        pos = text.find(lit)
        if pos < 0:
            continue
        line_no = text.count("\n", 0, pos) + 1
        lines = text.split("\n")
        # 取「生產點」那一行；同一行太長就截斷——README 的既有條目也是這樣處理的。
        src_line = lines[line_no - 1].strip()
        if len(src_line) > 240:
            src_line = src_line[:240] + "…"
        # 往上找緊鄰的註解行（最多兩行）
        comments = []
        i = line_no - 2
        while i >= 0 and len(comments) < 2:
            s = lines[i].strip()
            if s.startswith("//"):
                comments.insert(0, s.lstrip("/").strip())
                i -= 1
            else:
                break
        body = f'- **`{key}`** — `{rel}:{line_no}`（提及處）'
        for c in comments:
            body += f"\n  - 註解：{c}"
        body += f"\n  - 原始碼：`{src_line}`"
        return body
    return f'- **`{key}`** — （原始碼裡找不到字面；可能是動態家族成員）'


# ── 解析現有 README ──────────────────────────────────────────────────────

def slice_section(text, start, end):
    i = text.find(start)
    if i < 0:
        sys.exit(f"README 找不到區段「{start}」")
    j = text.find(end, i)
    if j < 0:
        sys.exit(f"README 找不到「{start}」之後的結束標記「{end}」")
    return i, j, text[i:j]


def parse_63(section, nl):
    """回 (head, {domain: (suffix, prose, {key: (name, src, trust)})})。"""
    parts = re.split(r'(?m)^### 6\.3\.\d+ 域 ', section)
    head = parts[0]
    out = {}
    for b in parts[1:]:
        m = re.match(r'`([^`]+)`([^\r\n]*)', b)
        if not m:
            continue
        domain, suffix = m.group(1), m.group(2)
        lines = b.split(nl)[1:]
        ti = next((x for x, l in enumerate(lines) if l.startswith("| 鍵 |")), None)
        prose = nl.join(lines[:ti]) if ti is not None else ""
        meta = {}
        for row in re.finditer(r'(?m)^\| `([^`]+)` \| ([^|]*) \| ([^|]*) \| ([^|]*) \|', b):
            meta[row.group(1)] = (row.group(2).strip(), row.group(3).strip(), row.group(4).strip())
        out[domain] = (suffix, prose, meta)
    return head, out


def parse_65(section, nl):
    """回 (head, {key: 條目原文})。"""
    parts = re.split(r'(?m)^#### 6\.5\.\d+ 域 `[^`]+`（\d+ 把）', section)
    head = parts[0]
    bullets = {}
    for body in parts[1:]:
        for m in re.finditer(r'(?m)^- \*\*`([^`]+)`\*\*[^\r\n]*(?:\r?\n  - [^\r\n]*)*', body):
            bullets[m.group(1)] = m.group(0)
    return head, bullets


def parse_appendix_f(section):
    """回 {domain: 說明欄}。"""
    out = {}
    for m in re.finditer(r'(?m)^\| `([^`]+)` \| \d+ \| `[^`]+` \| `[^`]+` \| ([^|]*) \|', section):
        out[m.group(1)] = m.group(2).strip()
    return out


# ── 生成 ─────────────────────────────────────────────────────────────────

def build_63(head, parsed, domains, corpus, nl):
    out = [head.rstrip(nl)]
    for n, (domain, keys) in enumerate(domains, 1):
        suffix, prose, meta = parsed.get(domain, ("", "", {}))
        out.append("")
        if suffix:
            out.append(f"### 6.3.{n} 域 `{domain}`{suffix}")
        else:
            out.append(f"### 6.3.{n} 域 `{domain}`（本版沒有成文的域說明）（{len(keys)} 把）")
        out.append("")
        if prose.strip():
            out.append(prose.strip(nl))
            out.append("")
        elif not suffix:
            out.append("這個域在本版沒有一句話的說明——它的名字是原始碼裡的前綴，"
                       "而前人沒有替它寫過定位。下表照實列出它的鍵與可判定的後設資料，"
                       "**不補一段聽起來合理的說明**。")
            out.append("")
        out.append("| 鍵 | 名稱（原始碼字面） | 來源（原始碼字面） | 可信度 |")
        out.append("|---|---|---|---|")
        for k in keys:
            name, src, trust = meta.get(k) or extract_meta(corpus, k)
            out.append(f"| `{k}` | {cell(name)} | {cell(src)} | {cell(trust)} |")
        out.append("")
    return normalize(nl.join(out), nl)


def build_65(head, bullets, domains, corpus, nl):
    out = [head.rstrip(nl)]
    for n, (domain, keys) in enumerate(domains, 1):
        out.append("")
        out.append(f"#### 6.5.{n} 域 `{domain}`（{len(keys)} 把）")
        out.append("")
        for k in keys:
            out.append(bullets.get(k) or extract_bullet(corpus, k))
        out.append("")
    return normalize(nl.join(out), nl)


def build_appendix_f(descriptions, domains, total_keys, nl):
    big = [(d, len(k)) for d, k in domains if len(k) >= 20]
    out = [
        f"### 附錄 F　{len(domains)} 個域的前綴表",
        "",
        "這張表是鍵空間的地圖：**域、鍵數、字典序首鍵與末鍵、以及這一域有沒有成文說明**。"
        "「說明」欄為空代表本版沒有替這一域寫過定位——那是一件該被看見的事，不是排版問題。",
        "",
        "| 域 | 鍵數 | 首鍵 | 末鍵 | 說明（既有文件） |",
        "|---|---:|---|---|---|",
    ]
    for domain, keys in domains:
        out.append(f"| `{domain}` | {len(keys)} | `{keys[0]}` | `{keys[-1]}` | "
                   f"{cell(descriptions.get(domain, ''))} |")
    out.append("")
    in_big = sum(c for _, c in big)
    out.append(f"**總計**：{len(domains)} 個域、{total_keys:,} 把鍵"
               f"（{in_big:,} 把落在 ≥20 鍵的 {len(big)} 個大域，"
               f"其餘 {total_keys - in_big:,} 把散在 {len(domains) - len(big)} 個域）。")
    out.append("")
    return normalize(nl.join(out), nl)


def build_appendix_f2(root, nl):
    """附錄 F-2 直接由動態家族登記重生（理由逐字照抄，不改寫）。"""
    text = read(os.path.join(root, FAMILIES))
    pairs = re.findall(
        r'^\s{12}\["([^"]+)"\] =\s*\n?\s*((?:"[^"]*"\s*\+?\s*\n?\s*)+),', text, re.M)
    families = []
    for prefix, raw in pairs:
        reason = "".join(re.findall(r'"([^"]*)"', raw)).strip()
        families.append((prefix, reason))
    families.sort(key=lambda x: x[0])
    if not families:
        sys.exit("動態家族解析出 0 條——格式變了，先修這支工具")

    out = [
        f"### 附錄 F-2　{len(families)} 個動態家族與「成員由什麼決定」",
        "",
        f"動態家族的成員在編譯期數不出來，所以目錄只登記前綴與理由。"
        f"下表是全部 {len(families)} 個前綴與它們各自申報的理由（**抽不到理由的會留白，不補**）。",
        "",
        "| 家族前綴 | 為什麼是動態家族／成員由什麼決定 |",
        "|---|---|",
    ]
    for prefix, reason in families:
        out.append(f"| `{prefix}` | {cell(reason)} |")
    return normalize(nl.join(out), nl)


def normalize(text, nl):
    """把區段收斂成穩定形狀：連續空行最多一行、結尾恰好一個空行。

    沒有這一步，重跑會一次多一個空行——那讓 `--check` 永遠有意見，
    而一個永遠在抱怨的工具等於沒有工具。
    """
    while nl * 3 in text:
        text = text.replace(nl * 3, nl * 2)
    return text.rstrip(nl) + nl + nl


def cell(text):
    """表格儲存格：內容裡的直線要跳脫，否則會把表格切壞（附錄 F-2 的理由欄真的踩過）。"""
    return text.replace("|", "\|")


def first_difference(a, b):
    """回第一個不同的行號與兩邊的內容（給 --check 的訊息用）。"""
    la, lb = a.split("\n"), b.split("\n")
    for i in range(min(len(la), len(lb))):
        if la[i] != lb[i]:
            return i + 1, la[i][:120], lb[i][:120]
    if len(la) != len(lb):
        i = min(len(la), len(lb))
        return i + 1, la[i][:120] if i < len(la) else "(檔案結束)", lb[i][:120] if i < len(lb) else "(檔案結束)"
    return None


def main():
    ap = argparse.ArgumentParser(description="重生 README 的逐域展開（6.3／6.5／附錄 F／F-2）")
    ap.add_argument("--check", action="store_true",
                    help="只比對不寫檔；有差異 exit 2")
    args = ap.parse_args()

    root = repo_root()
    path = os.path.join(root, README)
    text = read(path)
    nl = "\r\n" if "\r\n" in text else "\n"

    keys = load_keys(root)
    domains = group_domains(keys)
    corpus = source_corpus(root)

    i63, j63, sec63 = slice_section(text, SEC_63_START, SEC_63_END)
    i65, j65, sec65 = slice_section(text, SEC_65_START, SEC_65_END)
    iF, jF, secF = slice_section(text, APP_F_START, APP_F_END)
    iF2, jF2, _ = slice_section(text, APP_F_END, "### 附錄 G")

    head63, parsed63 = parse_63(sec63, nl)
    head65, bullets = parse_65(sec65, nl)
    descriptions = parse_appendix_f(secF)

    new63 = build_63(head63, parsed63, domains, corpus, nl)
    new65 = build_65(head65, bullets, domains, corpus, nl)
    newF = build_appendix_f(descriptions, domains, len(keys), nl)
    newF2 = build_appendix_f2(root, nl)

    # 由後往前換，避免位移
    result = text
    for (i, j, old, new, name) in sorted(
            [(i63, j63, sec63, new63, "6.3"),
             (i65, j65, sec65, new65, "6.5"),
             (iF, jF, secF, newF, "附錄 F"),
             (iF2, jF2, text[iF2:jF2], newF2, "附錄 F-2")],
            key=lambda x: -x[0]):
        if old == new:
            continue
        result = result[:i] + new + result[j:]
        if args.check:
            where = first_difference(old, new)
            line, a, b = where if where else (0, "", "")
            print(f"DRIFT: {name} 與目錄不同（區段內第 {line} 行）")
            print(f"  README: {a}")
            print(f"  應為  : {b}")

    if args.check:
        if result != text:
            print(f"\nREADME 的逐域展開落後於事實鍵目錄（{len(keys)} 把鍵、{len(domains)} 個域）。")
            print("修正方式：python Tools/gen-readme-domains.py")
            return 2
        print(f"OK：6.3／6.5／附錄 F／F-2 與目錄一致（{len(keys)} 把鍵、{len(domains)} 個域、"
              f"{len(load_families(root))} 個家族）")
        return 0

    if result != text:
        write(path, result)
        print(f"已重生 README 的逐域展開：{len(keys)} 把鍵、{len(domains)} 個域。")
    else:
        print("沒有變更（README 已與目錄一致）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
