#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把倉庫的文件（README.md／手冊.md）轉成自足的 HTML，供翻閱與定位。

為什麼需要這支：

  Markdown 在 GitHub 上有目錄、有錨點、能 Ctrl+F；一旦脫離 GitHub（離線、單檔發佈、
  內嵌進程式）就什麼都沒有——1.46MB／12,549 行的純文字沒有人能「翻閱定位」。
  這支工具把同一份 .md 產生一個**自足**的 HTML：章節目錄（可摺疊）、每級標題都有
  錨點、無 CDN、無外部字型、**無指令碼**（目錄用 <details>，不需要 JavaScript）。

用法（在倉庫根，或任意位置——腳本會自己找根）：

  python Tools/gen-docs-html.py            # 就地重生 百科.html／手冊.html
  python Tools/gen-docs-html.py --check    # 只比對，不寫檔；有差異 exit 2

**同一份來源只有一個渲染器**：HTML 一律由這支工具從 .md 產生，不手改 HTML、
也不在別處（程式裡）再寫一套 Markdown 渲染。HTML 檔頭寫明來源與來源的 sha256，
`--check` 與 Tests/DocsHtmlTests 用它對帳：改了 .md 沒重跑產生器＝紅燈。

錨點採 GitHub 的規則（小寫、去標點、空格轉連字號、重複加 -1/-2）。README 的目錄
本來就是照這個規則寫的內部連結，所以規則一旦跑偏，文件自己的目錄會先死——
`--check` 附帶一條自查：文件裡所有 `#錨點` 形式的連結都要能在產生結果裡找到對應 id。

支援的 Markdown 構文是「這兩份文件實際用到的那部分」：各級標題、段落、
無序／有序清單（含一層巢狀）、表格（含對齊與 \| 逸出）、引用區塊、``` 圍欄程式碼、
行內 `程式碼`、**粗體**、*斜體*、連結、徽章圖片（img.shields.io 轉成本地文字徽章）、
分隔線。其餘構文（HTML 區塊、腳註、定義清單）不在範圍內：出現就照原文轉義顯示，
不會悄悄吃字。
"""

import argparse
import hashlib
import html as htmlmod
import os
import re
import sys

# ── 文件對：來源 .md → 輸出 .html ────────────────────────────────────────
DOCS = [
    ("README.md", "百科.html", "曦覽 XinSpect 百科"),
    ("手冊.md", "手冊.html", "曦覽 XinSpect 手冊"),
]

REPO_URL = "https://github.com/Xinglanclever/XinSpect/blob/main/"

CODE_TOKEN = "\x00CODE%d\x00"
TABLE_CELL_SEP = "\x01"


def repo_root():
    d = os.path.dirname(os.path.abspath(__file__))
    while True:
        if os.path.exists(os.path.join(d, "XinSpect.csproj")):
            return d
        parent = os.path.dirname(d)
        if parent == d:
            raise SystemExit("找不到倉庫根（往上沒有 XinSpect.csproj）")
        d = parent


def normalize(text):
    """統一換行為 LF。來源 .md 在 Windows 工作副本可能是 CRLF，版控與 CI 是 LF。"""
    return text.replace("\r\n", "\n").replace("\r", "\n")


# ── 錨點：GitHub 規則 ─────────────────────────────────────────────────────

def plain_text(text):
    """標題的純文字（去行內程式碼反引號、去連結語法、去強調符號），供錨點與目錄用。"""
    t = re.sub(r"`([^`]*)`", r"\1", text)
    t = re.sub(r"!\[([^\]]*)\]\([^)]*\)", r"\1", t)
    t = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", t)
    t = re.sub(r"\*\*([^*]*)\*\*", r"\1", t)
    t = re.sub(r"(?<!\*)\*([^*]+)\*(?!\*)", r"\1", t)
    return t.strip()


def slugify(text, seen):
    s = plain_text(text).lower()
    out = []
    for ch in s:
        if ch.isalnum() or ch in "-_":
            out.append(ch)
        elif ch.isspace():
            out.append("-")          # 每個空白字元各轉一個連字號（GitHub 不合併，全形空白也算）
        # 其餘（標點、全形符號、emoji）照 GitHub 丢掉
    s = "".join(out)
    base, n = s, 0
    while s in seen:
        n += 1
        s = "%s-%d" % (base, n)
    seen.add(s)
    return s


# ── 行內渲染 ─────────────────────────────────────────────────────────────

def inline(text):
    """行內 Markdown → HTML。先抽出行內程式碼（裡面的 < > & 不能當標籤），再逐項替換。"""
    codes = []

    def stash_code(m):
        codes.append(m.group(2))
        return CODE_TOKEN % (len(codes) - 1)

    # `code`／``code``（這兩份文件沒有巢狀反引號）
    text = re.sub(r"(`+)(.+?)\1", lambda m: stash_code(m) if len(m.group(1)) <= 2 else m.group(0), text)

    text = htmlmod.escape(text, quote=False)

    # 徽章圖片 ![alt](https://img.shields.io/badge/label-message-color) → 本地文字徽章
    def badge(m):
        alt, url = m.group(1), m.group(2)
        msg = None
        mm = re.search(r"/badge/([^/]+)", url)
        if mm:
            parts = mm.group(1).split("-")
            if len(parts) >= 3:
                msg = "-".join(parts[1:-1]).replace("%20", " ").replace("--", "-")
        label = htmlmod.escape(alt or "", quote=False)
        value = htmlmod.escape(msg or "", quote=False)
        body = ("%s ・ %s" % (label, value)) if value else label
        return '<span class="badge">%s</span>' % body

    text = re.sub(r"!\[([^\]]*)\]\(([^)\s]+)\)", badge, text)

    # 連結 [text](target)
    def link(m):
        label, target = m.group(1), m.group(2)
        href = rewrite_target(target)
        title = ''
        mt = re.match(r"^(\S+)\s+\"(.*)\"$", target)
        if mt:
            href = rewrite_target(mt.group(1))
            title = ' title="%s"' % htmlmod.escape(mt.group(2), quote=True)
        if href is None:                      # 无法重写的相对路径：原样留文字，不造死链
            return "[%s](%s)" % (label, target)
        return '<a%s href="%s"%s>%s</a>' % (' class="ext"' if target.startswith("http") else "",
                                            htmlmod.escape(href, quote=True), title, label)

    text = re.sub(r"(?<!!)\[([^\]]+)\]\(([^)]+)\)", link, text)

    # 粗體／斜體
    text = re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", text)
    text = re.sub(r"(?<!\*)\*([^*\n]+)\*(?!\*)", r"<em>\1</em>", text)

    for i, code in enumerate(codes):
        text = text.replace(CODE_TOKEN % i, "<code>%s</code>" % htmlmod.escape(code, quote=False))
    return text


def rewrite_target(target):
    """相對目標改寫：文件對的 .md 轉到對應 .html；其他倉庫內相對路徑轉到 GitHub；
    錨點、mailto 與絕對 URL 原樣。回傳 None 表示重寫不了，呼叫端保留原文。"""
    if target.startswith("#") or target.startswith("mailto:"):
        return target
    if re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*:", target):
        return target
    path, _, frag = target.partition("#")
    if not path:
        return "#" + frag
    for src, out, _title in DOCS:
        if path.lower() == src.lower():
            path = out
            break
    else:
        if not path.startswith("/") and os.path.basename(path) in {out for _s, out, _t in DOCS}:
            pass                                      # 同目錄的另一份 HTML：原樣
        elif path.startswith("/"):
            return None
        else:
            path = REPO_URL + path.replace("\\", "/")
    return path + ("#" + frag if frag else "")


# ── 區塊渲染 ─────────────────────────────────────────────────────────────

def is_table_sep(line):
    return bool(re.match(r"^\|?[\s:|-]+\|[\s:|-]*$", line)) and "-" in line


def split_row(line):
    line = line.strip()
    if line.startswith("|"):
        line = line[1:]
    if line.endswith("|") and not line.endswith("\\|"):
        line = line[:-1]
    line = line.replace("\\|", TABLE_CELL_SEP)
    return [c.strip().replace(TABLE_CELL_SEP, "|") for c in line.split("|")]


def render_table(block):
    header = split_row(block[0])
    aligns = [c.strip() for c in split_row(block[1])]
    cols = []
    for a in aligns:
        if a.startswith(":") and a.endswith(":"):
            cols.append(' style="text-align:center"')
        elif a.endswith(":"):
            cols.append(' style="text-align:right"')
        else:
            cols.append("")
    while len(cols) < len(header):
        cols.append("")
    out = ['<table>', '<thead><tr>']
    for i, cell in enumerate(header):
        out.append("<th%s>%s</th>" % (cols[i] if i < len(cols) else "", inline(cell)))
    out.append("</tr></thead><tbody>")
    for row in block[2:]:
        cells = split_row(row)
        out.append("<tr>%s</tr>" % "".join(
            "<td%s>%s</td>" % (cols[i] if i < len(cols) else "", inline(c)) for i, c in enumerate(cells)))
    out.append("</tbody></table>")
    return "\n".join(out)


def render_list(block):
    """block: list of (indent, marker_type, content). 只支援兩層巢狀（這兩份文件的實際深度）。"""
    def item_html(indent, marker, content):
        tag = "ol" if marker == "ol" else "ul"
        return indent, tag, content

    out = []
    stack = []          # [(indent, tag)]

    def close_to(indent):
        while stack and stack[-1][0] >= indent:
            out.append("</li></%s>" % stack.pop()[1])

    for i, (indent, marker, content) in enumerate(block):
        tag = "ol" if marker == "ol" else "ul"
        if not stack or indent > stack[-1][0]:
            out.append("<%s>" % tag)
            stack.append((indent, tag))
            out.append("<li>%s" % inline(content))
        else:
            if indent < stack[-1][0]:
                close_to(indent)
            out.append("</li><li>%s" % inline(content))
        if tag == "ol" and i + 1 < len(block):
            pass
    while stack:
        out.append("</li></%s>" % stack.pop()[1])
    return "\n".join(out)


class Renderer:
    def __init__(self):
        self.seen = set()
        self.headings = []          # (level, slug, text)

    def heading(self, level, text):
        s = slugify(text, self.seen)
        self.headings.append((level, s, plain_text(text)))
        return '<h%(l)s id="%(s)s"><a class="self" href="#%(s)s">¶</a> %(t)s</h%(l)s>' % {
            "l": level, "s": s, "t": inline(text)}

    def render(self, lines):
        out = []
        i, n = 0, len(lines)
        para = []

        def flush_para():
            if para:
                out.append("<p>%s</p>" % "<br>\n".join(inline(l) for l in para))
                para.clear()

        while i < n:
            line = lines[i]
            stripped = line.strip()

            if not stripped:
                flush_para()
                i += 1
                continue

            m = re.match(r"^(#{1,6})\s+(.*)$", line)
            if m:
                flush_para()
                out.append(self.heading(len(m.group(1)), m.group(2).strip()))
                i += 1
                continue

            if re.match(r"^\s*(-{3,}|\*{3,}|_{3,})\s*$", line):
                flush_para()
                out.append("<hr>")
                i += 1
                continue

            if stripped.startswith("```"):
                flush_para()
                lang = stripped[3:].strip()
                buf = []
                i += 1
                while i < n and not lines[i].strip().startswith("```"):
                    buf.append(lines[i])
                    i += 1
                i += 1                                     # 吃掉結束的 ```
                out.append('<pre><code%s>%s</code></pre>' % (
                    ' class="lang-%s"' % lang if lang else "", htmlmod.escape("\n".join(buf), quote=False)))
                continue

            if stripped.startswith("|") and i + 1 < n and is_table_sep(lines[i + 1].strip()):
                flush_para()
                block = [lines[i], lines[i + 1]]
                i += 2
                while i < n and lines[i].strip().startswith("|"):
                    block.append(lines[i])
                    i += 1
                out.append(render_table(block))
                continue

            if stripped.startswith(">"):
                flush_para()
                buf = []
                while i < n and lines[i].strip().startswith(">"):
                    buf.append(re.sub(r"^\s*>\s?", "", lines[i]))
                    i += 1
                out.append("<blockquote>%s</blockquote>" % "\n".join(
                    Renderer().render_inner(buf)))
                continue

            m = re.match(r"^(\s*)([-+*])\s+(.*)$", line) or re.match(r"^(\s*)(\d+\.)\s+(.*)$", line)
            if m:
                flush_para()
                block = []
                while i < n:
                    cur = lines[i]
                    mm = re.match(r"^(\s*)([-+*]|\d+\.)\s+(.*)$", cur)
                    if not mm:
                        if not cur.strip():
                            nxt = lines[i + 1] if i + 1 < n else ""
                            if re.match(r"^\s*([-+*]|\d+\.)\s+", nxt):
                                i += 1
                                continue
                        break
                    indent = len(mm.group(1))
                    marker = "ol" if mm.group(2)[0].isdigit() else "ul"
                    content = mm.group(3)
                    # 軟性換行（縮排的續行）併進同一個項目
                    j = i + 1
                    while j < n and lines[j].strip() and not re.match(r"^\s*([-+*]|\d+\.)\s+", lines[j]) \
                            and not re.match(r"^#{1,6}\s", lines[j]) and not lines[j].strip().startswith("|"):
                        content += " " + lines[j].strip() if re.match(r"^\s{2,}", lines[j]) else "\n" + lines[j].strip()
                        j += 1
                    block.append((indent, marker, content))
                    i = j
                out.append(render_list(block))
                continue

            if stripped.startswith("<") and re.match(r"^</?[a-zA-Z]", stripped) and not stripped.startswith("<http"):
                # 罕見：區塊級原始 HTML（行內標籤已在行內處理裡被轉義成文字）
                flush_para()
                out.append(stripped)
                i += 1
                continue

            para.append(stripped)
            i += 1

        flush_para()
        return out

    def render_inner(self, lines):
        return self.render(lines)


TOC_MAX_LEVEL = 3


def build_toc(headings):
    """目錄：h2 為一組，底下掛 h3（用 <details> 摺疊，無 JS）。h1 當做頁面主標題不進目錄。"""
    out = []
    open_details = False
    for level, s, text in headings:
        if level == 1:
            continue
        if level == 2:
            if open_details:
                out.append("</details>")
            out.append('<details class="toc-part"%s><summary><a href="#%s">%s</a></summary>'
                       % (" open" if not open_details and len([h for h in headings if h[0] == 2]) > 200 else "",
                          htmlmod.escape(s, quote=True), htmlmod.escape(text, quote=False)))
            open_details = True
        elif level == 3 and open_details:
            out.append('<a class="toc-sub" href="#%s">%s</a>'
                       % (htmlmod.escape(s, quote=True), htmlmod.escape(text, quote=False)))
    if open_details:
        out.append("</details>")
    return "\n".join(out)


STYLE = """
:root{--bg:#fff;--ink:#1f2328;--sub:#57606a;--hair:#d0d7de;--surface:#f6f8fa;--link:#0969da;--accent:#1b6fd6;--badge:#ddf4ff;
      color-scheme:light}
/* 深色版：文字色早就跟著 prefers-color-scheme 換了，body 的背景卻一直寫死 #fff——
   結果在深色介面（應用程式內嵌的 WebView2、或深色模式瀏覽器）變成近白字壓在白底上。
   背景現在也是變數，兩邊一起換。 */
@media (prefers-color-scheme:dark){:root{--bg:#0d1117;--ink:#e6edf3;--sub:#9198a1;--hair:#30363d;--surface:#161b22;
      --link:#4493f8;--accent:#4493f8;--badge:#0d2b45;color-scheme:dark}}
*{box-sizing:border-box}
body{margin:0;font-family:"Segoe UI","Microsoft JhengHei","Noto Sans TC",system-ui,sans-serif;
     color:var(--ink);background:var(--bg);line-height:1.7;font-size:15.5px}
nav.toc{position:fixed;top:0;left:0;width:322px;height:100vh;overflow-y:auto;padding:18px 12px 40px 18px;
        border-right:1px solid var(--hair);background:var(--surface);font-size:13px}
nav.toc h2{font-size:14px;margin:0 0 10px}
nav.toc a{color:var(--ink);text-decoration:none;display:block;padding:2px 0}
nav.toc a:hover{color:var(--accent);text-decoration:underline}
nav.toc details{margin:0 0 2px}
nav.toc summary{cursor:pointer;font-weight:600;list-style-position:outside;padding:2px 0}
nav.toc summary a{display:inline;font-weight:600}
nav.toc .toc-sub{margin-left:14px;color:var(--sub);font-size:12.5px}
main{margin-left:322px;padding:26px 40px 80px;max-width:1080px}
header.doc{border-bottom:2px solid var(--hair);padding-bottom:12px;margin-bottom:22px}
header.doc .meta{color:var(--sub);font-size:12.5px}
h1{font-size:29px;margin:6px 0}
h2{font-size:22px;margin:34px 0 12px;padding-bottom:6px;border-bottom:1px solid var(--hair)}
h3{font-size:18px;margin:26px 0 8px}
h4{font-size:16px;margin:20px 0 6px}
h1,h2,h3,h4{scroll-margin-top:14px}
a.self{opacity:0;text-decoration:none;margin-right:6px;font-weight:400}
h1:hover a.self,h2:hover a.self,h3:hover a.self,h4:hover a.self{opacity:.45}
a{color:var(--link)}
p{margin:9px 0}
ul,ol{margin:8px 0;padding-left:26px}
li{margin:3px 0}
blockquote{margin:12px 0;padding:6px 16px;border-left:4px solid var(--accent);
           background:var(--surface);color:var(--sub)}
code{font-family:"Cascadia Mono",Consolas,monospace;font-size:.88em;background:var(--surface);
     padding:1px 5px;border-radius:4px;border:1px solid var(--hair)}
pre{background:var(--surface);border:1px solid var(--hair);border-radius:8px;padding:14px;overflow-x:auto}
pre code{border:0;background:transparent;padding:0}
table{border-collapse:collapse;margin:14px 0;font-size:14px;display:block;overflow-x:auto;max-width:100%}
th,td{border:1px solid var(--hair);padding:6px 10px;vertical-align:top;text-align:left}
th{background:var(--surface);font-weight:600;white-space:nowrap}
td code{white-space:nowrap}
hr{border:0;border-top:1px solid var(--hair);margin:26px 0}
.badge{display:inline-block;background:var(--badge);border:1px solid var(--hair);border-radius:5px;
       padding:1px 8px;margin:2px 6px 2px 0;font-size:12.5px;white-space:nowrap}
.top{position:fixed;right:22px;bottom:22px;background:var(--surface);border:1px solid var(--hair);
     border-radius:6px;padding:5px 11px;font-size:12.5px;text-decoration:none}
@media (max-width:1000px){nav.toc{position:static;width:auto;height:auto;border-right:0}
 main{margin-left:0;padding:18px}}
"""


def render_doc(root, src, out_name, title):
    # 先讀位元組、統一換行（CRLF／CR → LF）再算雜湊：Windows 工作副本是 CRLF、版控與
    # CI（Linux）是 LF，直接雜湊位元組會讓同一份文件在兩邊算出不同值，`--check` 就會
    # 在 CI 上假紅。雜湊的是「內容」，不是「哪個作業系統寫的換行」。
    with open(os.path.join(root, src), "rb") as fh:
        blob = fh.read()
    raw = normalize(blob.decode("utf-8-sig"))
    digest = hashlib.sha256(raw.encode("utf-8")).hexdigest()[:12]
    lines = raw.split("\n")

    r = Renderer()
    body = r.render(lines)
    toc = build_toc(r.headings)

    parts = [
        "<!DOCTYPE html>",
        '<html lang="zh-Hant">',
        "<head>",
        '<meta charset="utf-8">',
        '<meta name="viewport" content="width=device-width, initial-scale=1">',
        "<title>%s</title>" % htmlmod.escape(title, quote=True),
        "<!-- 由 %s 產生（sha256 %s）；產生器：Tools/gen-docs-html.py；本檔不手改。 -->" % (src, digest),
        "<style>%s</style>" % STYLE.strip(),
        "</head>",
        "<body>",
        '<nav class="toc" id="toc">',
        "<h2>目錄</h2>",
        toc,
        "</nav>",
        "<main>",
        '<header class="doc"><div class="meta">%s ・ 來源：<code>%s</code>（sha256 <code>%s</code>）'
        '・ 自足 HTML：無 CDN、無外部字型、無指令碼 ・ 定位：左側目錄、標題懸停有 ¶ 錨點、瀏覽器 Ctrl+F 找字</div></header>'
        % (htmlmod.escape(title, quote=False), src, digest),
        "\n".join(body),
        '<a class="top" href="#toc">回到目錄</a>',
        "</main>",
        "</body>",
        "</html>",
        "",
    ]
    return "\n".join(parts), src, digest


def check_anchors(html_text, md_text):
    """文件自己的 #錨點 連結對得上幾個——對不上的是「文件目錄本身落後」，
    不是 slug 演算法壞了；只當警告列出來，不讓它擋住產生器。"""
    ids = set(re.findall(r'id="([^"]+)"', html_text))
    bad = []
    for m in re.finditer(r'\]\(#([^)]+)\)', md_text):
        want = m.group(1)
        if want not in ids:
            bad.append(want)
    return bad


# 固定案例：從文件的實際目錄連結反推出來的 GitHub 錨點規則。
# 演算法一旦跑偏，這裡先紅——比等到有人點目錄才發現好。
SLUG_CASES = [
    ("這棵樹的事實表（可複算）", "這棵樹的事實表可複算"),
    ("第 0 部 導讀：這份文件的詞彙、六態、四級可信度與誤差語彙",
     "第-0-部-導讀這份文件的詞彙六態四級可信度與誤差語彙"),
    ("0.1 三種讀者，三條路徑", "01-三種讀者三條路徑"),
    ("附錄 F　150 個域的前綴表", "附錄-f-150-個域的前綴表"),
    ("附錄 G　全站說明條目逐條（285 條）", "附錄-g-全站說明條目逐條285-條"),
    ("`code` 與 **粗體** 標題", "code-與-粗體-標題"),
]


def self_test_slugs():
    bad = []
    for text, want in SLUG_CASES:
        got = slugify(text, set())
        if got != want:
            bad.append("%r → %r（預期 %r）" % (text, got, want))
    return bad


def main():
    ap = argparse.ArgumentParser(description="把 .md 文件轉成自足 HTML（可翻閱、可定位）")
    ap.add_argument("--check", action="store_true", help="只比對，不寫檔；有差異 exit 2")
    args = ap.parse_args()

    root = repo_root()
    rc = 0
    for msg in self_test_slugs():
        print("slug 固定案例失敗：" + msg, file=sys.stderr)
        rc = 2
    for src, out_name, title in DOCS:
        html_text, src_name, digest = render_doc(root, src, out_name, title)
        bad = check_anchors(html_text, open(os.path.join(root, src), encoding="utf-8-sig").read())
        if bad:
            print("提醒：%s 有 %d 個目錄錨點對不上任何標題（多半是目錄落後），前 5 個：%s"
                  % (src, len(bad), "、".join(bad[:5])), file=sys.stderr)
        path = os.path.join(root, out_name)
        existing = None
        if os.path.exists(path):
            existing = normalize(open(path, encoding="utf-8", newline="").read())
        if args.check:
            if existing is None:
                print("%s 不存在——請跑 python Tools/gen-docs-html.py" % out_name, file=sys.stderr)
                rc = 2
            elif existing != html_text:
                print("%s 與 %s 不同步（%d → %d bytes）——請重跑產生器"
                      % (out_name, src, len(existing), len(html_text)), file=sys.stderr)
                rc = 2
            else:
                print("OK：%s 與 %s 同步（%s bytes，sha256 %s，%d 個錨點）"
                      % (out_name, src, f"{len(html_text):,}", digest, html_text.count('<h')))
        else:
            with open(path, "w", encoding="utf-8", newline="\n") as fh:
                fh.write(html_text)
            print("寫入 %s：%s bytes（來源 %s，sha256 %s）"
                  % (out_name, f"{len(html_text):,}", src, digest))
    return rc


if __name__ == "__main__":
    sys.exit(main())
