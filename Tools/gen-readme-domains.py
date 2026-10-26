#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""README 逐域展開的產生器（第 282 輪；第 283 輪補上目錄的 6.3 條目）。

README 的第 6.3 節（逐域鍵表）、第 6.5 節（逐鍵出處）、附錄 F（域前綴表）與
附錄 F-2（動態家族）是把事實鍵目錄**整份展開**——149 個域、1,131 把鍵。那份內容原本由一次性腳本寫成，
改完目錄沒重跑就與原始碼脫節，而且沒有任何東西會叫（第 279 輪補了結構守門，
但守門只驗「有沒有漏或多」，不會幫你把漏的補上）。

這支工具做兩件事：

  python Tools/gen-readme-domains.py            # 就地重生（只動它負責的區段）
  python Tools/gen-readme-domains.py --check    # 只比對，不寫檔；有差異 exit 2

**只動它負責的四個區段＋目錄的 6.3 條目**，其餘一個字都不碰。

目錄那一塊是第 283 輪補的：條目編號與錨點原本靠手維護，於是「章節重編號」與
「目錄」各自漂移——v2.99 時目錄寫 `6.3.57 域 kp`、章節已經是 `6.3.58`，
而沒有任何測試看得出來。已有的列／條目**原樣保留**
（名稱、來源、可信度、註解、原始碼片段都是人工與工具協作的結果，重跑不該洗掉），
只有目錄裡新出現、文件裡還沒有的鍵才由原始碼抽後設資料補上——
抽不到就留「—」，那是如實的「這個抽取方式抓不到」，不是遺漏。

**第 288 輪補的兩件事**：①建構子的參數逐一解析（字串逸出與括號深度都照實際走），
不再用「不能含逗號」的正規表示式硬切——`$"{ms / 1000.0:F1} 秒（{ms} ms）"` 這種值
會讓舊寫法把「名字明明寫死」的呼叫判成抽不到；②第一參數是常數（
`new HardwareFact(TablesKey, …)`）也認得，同檔案的 `const string` 會被解析。
效果：§6.3「名稱／來源／可信度」全填的列從 112 升到 848（共 1,131 列）。
已經存在的非「—」格子**一律不覆蓋**；「—」視為「還沒抽到」而不是「確定是空」。

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


def const_values(corpus):
    """常數名 → 字串值（`public const string XxxKey = "kp.foo.bar";`）。

    <b>為什麼需要這一層：</b>大部分服務不是把鍵字面塞進建構子，而是先宣告常數
    （`public const string TablesKey = "kp.builtin.tables";`）再 `new HardwareFact(TablesKey, …)`。
    只認字面的抽取法因此對 1,131 把鍵裡的 839 把什麼都抽不到——§6.3 的
    「名稱／來源／可信度」欄於是幾乎全是「—」。§6.3 該欄的定義是「原始碼字面」，
    這些值確實是原始碼裡的固定字串，只是隔了一層常數。
    """
    out = {}
    const = re.compile(r'const\s+string\s+(\w+)\s*=\s*"([^"]+)"')
    for _, text in corpus:
        for m in const.finditer(text):
            out.setdefault(m.group(1), m.group(2))
    return out


def _split_args(text, start):
    """從 `(` 之後開始切參數；回 (參數清單, 收尾 `)` 的位置)。

    <b>為什麼不能用正規表示式硬切：</b>參數裡有逗號。`$"{ms / 1000.0:F1} 秒（{ms} ms）"`
    這種插值字串、`new X(a, b)` 這種巢狀呼叫都會讓 `[^,]+` 提前斷掉，
    於是「名字明明寫死在第三個參數」的呼叫被判定成抽不到（第 288 輪一次抓到二十幾把）。
    這裡照字串逸出與括號深度實際走一遍。
    """
    args, cur = [], []
    depth, i, in_str, esc = 0, start, False, False
    while i < len(text):
        ch = text[i]
        if in_str:
            cur.append(ch)
            if esc:
                esc = False
            elif ch == "\\":
                esc = True
            elif ch == '"':
                in_str = False
        elif ch == '"':
            in_str = True
            cur.append(ch)
        elif ch in "([{":
            depth += 1
            cur.append(ch)
        elif ch in ")]}":
            if depth == 0:
                if ch == ")":
                    args.append("".join(cur).strip())
                    return args, i
                return [], -1
            depth -= 1
            cur.append(ch)
        elif ch == "," and depth == 0:
            args.append("".join(cur).strip())
            cur = []
        else:
            cur.append(ch)
        i += 1
    return [], -1


def _one_literal(arg, consts):
    """單一段落 → 字串值：純字面或同檔常數；其餘回 None。"""
    if len(arg) >= 2 and arg[0] == '"' and arg[-1] == '"':
        body = arg[1:-1]
        if "{" in body or "$" in body or "\n" in body or "\r" in body:
            return None
        return body.replace('\\"', '"')
    if re.fullmatch(r"[A-Za-z_]\w*", arg) and arg in consts:
        value = consts[arg]
        return None if ("\n" in value or "\r" in value) else value
    return None


def _arg_literal(arg, consts):
    """參數 → 字串值。純字面、同檔常數，或**兩者的 `+` 串接**；其餘回 None。

    <b>不猜：</b>`$"…"`（值會變）、`x is not null ? A : B`（兩個答案）都必須回 None——
    留「—」比填一個只對一半的值誠實。

    <b>為什麼要處理串接：</b>長來源字串在原始碼裡常被折成兩行
    （`"…@0x09、" + "段群組@0x0B…"`）。舊寫法把整段當成「一個帶引號的字串」收下，
    於是**把換行與 `+` 原封不動寫進表格儲存格**，那個 `sb.onboard` 列直接破了表
    （第 288 輪一次抓到；表格破了但守門因為 `[^|]` 可以跨行而照樣綠）。
    """
    parts = _split_top(arg, "+")
    if len(parts) > 1:
        values = [_one_literal(part, consts) for part in parts]
        return None if any(v is None for v in values) else "".join(values)
    return _one_literal(arg, consts)


def _split_top(text, sep):
    """在括號與字串之外依 `sep` 切段（`_split_args` 的同一個走法，可指定分隔字元）。"""
    parts, cur = [], []
    depth, in_str, esc = 0, False, False
    for ch in text:
        if in_str:
            cur.append(ch)
            if esc:
                esc = False
            elif ch == "\\":
                esc = True
            elif ch == '"':
                in_str = False
        elif ch == '"':
            in_str = True
            cur.append(ch)
        elif ch in "([{":
            depth += 1
            cur.append(ch)
        elif ch in ")]}":
            depth -= 1
            cur.append(ch)
        elif ch == sep and depth == 0:
            parts.append("".join(cur).strip())
            cur = []
        else:
            cur.append(ch)
    parts.append("".join(cur).strip())
    return [p for p in parts if p != ""]


def ctor_meta(corpus):
    """鍵 → (名稱, 來源, 可信度)：逐檔解析建構子的參數。

    參數序：`new HardwareFact(Key, Category, Name, Value, Unit, Source, Trust, …)`。
    名稱取第 3 個、來源取第 6 個、可信度取第 7 個；任何一個不是「原始碼字面或同檔常數」
    就留 None（呼叫端寫成「—」）。
    """
    out = {}
    sites = {}
    # 兩種寫法分開處理，因為**可信度不同**：
    #   ①`new HardwareFact(…)`——型別寫出來了，參數序就是建構子的參數序，可以接受部分抽取；
    #   ②`new(…)`——只是清單初始化式的一項，**型別不外顯**。同一個檔案裡還有別的 record
    #     （例如 `os.ev.*` 那張表用的是私有 record，欄位完全不同），照樣會被這個模式抓到，
    #     於是把「該 record 的第 3 欄」當成名稱填進文件。第 288 輪真的發生過一列
    #     `os.ev.whea.internal` 的名稱變成「System」。
    #     所以第二種必須靠「第 7 個參數是 FactTrustLevel.X」認出它真的是 HardwareFact。
    typed = re.compile(r'new\s+HardwareFact\s*\(')
    anon = re.compile(r'(?<![\w.])new\s*\(')
    for _, text in corpus:
        consts = {}
        for m in re.finditer(r'const\s+string\s+(\w+)\s*=\s*"([^"]*)"', text):
            consts.setdefault(m.group(1), m.group(2))

        def take(match, require_trust):
            args, _ = _split_args(text, match.end())
            if len(args) < 7:
                return None
            key = _arg_literal(args[0], consts)
            if not key or not re.fullmatch(r"[a-z][a-z0-9_.]+", key):
                return None
            trust = re.fullmatch(r"FactTrustLevel\.(\w+)", args[6])
            if require_trust and not trust:
                return None          # 位置對不上＝不是 HardwareFact 的參數序，整筆跳過
            return key, (_arg_literal(args[2], consts),
                         _arg_literal(args[5], consts),
                         trust.group(1) if trust else None)

        for rx, require_trust in ((typed, False), (anon, True)):
            for m in rx.finditer(text):
                got = take(m, require_trust)
                if got is None:
                    continue
                key, value = got
                if any(v is not None for v in value):
                    sites.setdefault(key, []).append(value)

    # 同一把鍵常常有兩個建構點（「有值」那條與「讀不到」那條），可信度可以不一樣——
    # `nic.mac.0` 就有 Reported 與 Unknown 兩種。**兩個答案都對，但一欄只能放一個**：
    # 這時候留「—」，因為任選一個都是在替另一條路徑說話。
    for key, values in sites.items():
        merged = []
        for i in range(3):
            distinct = {v[i] for v in values if v[i] is not None}
            merged.append(distinct.pop() if len(distinct) == 1 else None)
        if any(v is not None for v in merged):
            out[key] = tuple(merged)
    return out


def extract_meta(key, ctor):
    """回 (名稱, 來源, 可信度)，抽不到的格子是「—」。

    鍵在原始碼裡可能是字面、也可能是常數——`ctor_meta` 兩種都認（第 288 輪）。
    """
    hit = ctor.get(key)
    if hit is None:
        return ("—", "—", "—")
    return tuple(v if v is not None else "—" for v in hit)


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

def effective_suffix(domain, keys, parsed):
    """標題尾巴——「（N 把）」一定要跟著目錄重算。

    它原本是照抄舊文字，於是「這一域多了三把鍵」會生出
    「標題寫 7 把、表裡列 10 列」的自我矛盾標題（第 283 輪的 kp 域）。
    """
    suffix = parsed.get(domain, ("", "", {}))[0]
    if not suffix:
        return f"（本版沒有成文的域說明）（{len(keys)} 把）"
    fixed, replaced = re.subn(r'（\d[\d,]* 把）\s*$', f'（{len(keys)} 把）', suffix)
    if not replaced:
        raise SystemExit(f"域 `{domain}` 的標題沒有「（N 把）」尾巴——"
                         "README 的形狀變了，產生器不猜要改哪裡。")
    return fixed


def build_63(head, parsed, domains, corpus, ctor, nl):
    out = [head.rstrip(nl)]
    for n, (domain, keys) in enumerate(domains, 1):
        documented = bool(parsed.get(domain, ("", "", {}))[0])
        suffix = effective_suffix(domain, keys, parsed)
        prose, meta = parsed.get(domain, ("", "", {}))[1:]
        out.append("")
        out.append(f"### 6.3.{n} 域 `{domain}`{suffix}")
        out.append("")
        if prose.strip():
            out.append(prose.strip(nl))
            out.append("")
        elif not documented:
            out.append("這個域在本版沒有一句話的說明——它的名字是原始碼裡的前綴，"
                       "而前人沒有替它寫過定位。下表照實列出它的鍵與可判定的後設資料，"
                       "**不補一段聽起來合理的說明**。")
            out.append("")
        out.append("| 鍵 | 名稱（原始碼字面） | 來源（原始碼字面） | 可信度 |")
        out.append("|---|---|---|---|")
        for k in keys:
            # 既有列的名稱／來源／可信度是人工協作結果，**不覆蓋**；
            # 但「—」代表「沒有值」而不是「確定是空」——第 288 輪起會重抽補上，
            # 否則產生器認得的新來源永遠填不進已經存在的那一列。
            saved = meta.get(k)
            fresh = extract_meta(k, ctor)
            name, src, trust = (
                (saved[0] if saved and saved[0] != "—" else fresh[0]),
                (saved[1] if saved and saved[1] != "—" else fresh[1]),
                (saved[2] if saved and saved[2] != "—" else fresh[2]),
            )
            out.append(f"| `{k}` | {cell(name)} | {cell(src)} | {cell(trust)} |")
        out.append("")
    return normalize(nl.join(out), nl)


def build_65(head, bullets, domains, corpus, nl):
    # 開頭那句「N 把鍵之中有 M 把在本版 Services/ 的字面中找得到位置」是現在式宣稱，
    # 兩個數字都會隨目錄變動——照抄舊文字就等於讓它安靜地變舊。
    all_keys = [k for _, ks in domains for k in ks]
    found = sum(1 for k in all_keys if any(('"' + k) in text for _, text in corpus))
    claim = re.compile(r'\d[\d,]* 把鍵之中有 \d[\d,]* 把')
    if not claim.search(head):
        raise SystemExit("6.5 的開頭找不到「N 把鍵之中有 M 把…找得到位置」這一句——"
                         "README 的形狀變了，產生器不猜要改哪裡。")
    head = claim.sub(f'{len(all_keys):,} 把鍵之中有 {found:,} 把', head, count=1)
    out = [head.rstrip(nl)]
    for n, (domain, keys) in enumerate(domains, 1):
        out.append("")
        out.append(f"#### 6.5.{n} 域 `{domain}`（{len(keys)} 把）")
        out.append("")
        for k in keys:
            out.append(bullets.get(k) or extract_bullet(corpus, k))
        out.append("")
    return normalize(nl.join(out), nl)


TOC63_RE = re.compile(r'(?m)^  - \[6\.3 逐域鍵表[^\r\n]*(?:\r?\n  - \[6\.3\.\d+ 域 [^\r\n]*)+')


def slugify(text):
    """GitHub 標題錨點：去掉反引號與標點、空白轉連字號、保留中文與底線。"""
    out = []
    for ch in text.replace("`", "").lower():
        if ch.isalnum() or ch in "-_":
            out.append(ch)
        elif ch in " \u00a0\u3000":
            out.append("-")
    return "".join(out)


def build_toc63(domains, parsed, total_keys, nl):
    """目錄的 6.3 條目——編號、字數、錨點全部由章節推導。"""
    rows = [f"  - [6.3 逐域鍵表（{len(domains)} 個域、{total_keys:,} 把鍵）]"
            f"(#63-逐域鍵表{len(domains)}-個域{total_keys}-把鍵)"]
    for n, (domain, keys) in enumerate(domains, 1):
        heading = f"6.3.{n} 域 `{domain}`{effective_suffix(domain, keys, parsed)}"
        rows.append(f"  - [{heading}](#{slugify(heading)})")
    return nl.join(rows)


def patch_count_lines(text, n_domains, total_keys, n_families, nl):
    """六行帶數字的標題／目錄行：只改這些行的數字，其餘一行不碰。

    逐行處理而不是全文替換——全文替換會把內文裡**歷史敘述**的舊數字一起改掉，
    那會讓「當時是多少」變成「現在是多少」。
    """
    def fix(line):
        if line.startswith("- [第 6 部 數據辭典：") or line.startswith("  - [6.3 逐域鍵表"):
            line = re.sub(r"\d+ 個域、[\d,]+ 把鍵", f"{n_domains} 個域、{total_keys:,} 把鍵", line)
            line = re.sub(r"-\d+-個域\d+-把鍵", f"-{n_domains}-個域{total_keys}-把鍵", line)
        elif line.startswith("## 第 6 部 數據辭典：") or line.startswith("### 6.3 逐域鍵表"):
            line = re.sub(r"\d+ 個域、[\d,]+ 把鍵", f"{n_domains} 個域、{total_keys:,} 把鍵", line)
        elif line.startswith("  - [附錄 F　"):
            line = re.sub(r"\d+ 個域的前綴表", f"{n_domains} 個域的前綴表", line)
            line = re.sub(r"#附錄-f-\d+", f"#附錄-f-{n_domains}", line)
        elif line.startswith("  - [附錄 F-2　"):
            line = re.sub(r"\d+ 個動態家族", f"{n_families} 個動態家族", line)
            line = re.sub(r"#附錄-f-2-\d+", f"#附錄-f-2-{n_families}", line)
        return line

    return nl.join(fix(line) for line in text.split(nl))


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

    ctor = ctor_meta(corpus)
    new63 = build_63(head63, parsed63, domains, corpus, ctor, nl)
    new65 = build_65(head65, bullets, domains, corpus, nl)
    newF = build_appendix_f(descriptions, domains, len(keys), nl)
    newF2 = build_appendix_f2(root, nl)

    mtoc = TOC63_RE.search(text)
    if not mtoc:
        sys.exit("找不到目錄的 6.3 條目區塊（`  - [6.3 逐域鍵表…` 起）——格式變了，先修這支工具")
    newToc = build_toc63(domains, parsed63, len(keys), nl)

    # 由後往前換，避免位移
    result = text
    for (i, j, old, new, name) in sorted(
            [(i63, j63, sec63, new63, "6.3"),
             (i65, j65, sec65, new65, "6.5"),
             (iF, jF, secF, newF, "附錄 F"),
             (iF2, jF2, text[iF2:jF2], newF2, "附錄 F-2"),
             (mtoc.start(), mtoc.end(), mtoc.group(0), newToc, "目錄 6.3")],
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

    result = patch_count_lines(result, len(domains), len(keys), len(load_families(root)), nl)

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
