# 官網版本同步器：讀 csproj 版號＋changelog.json 最新筆＋GitHub Release 實況，
# 把 site/ 兩頁的全部版本字串一次同步。
# 用法：python -I -X utf8 Tools/bump-site-version.py
# 依賴：先跑過 python -I Tools/gen-site-data.py（changelog.json 已是最新）。
import io, json, re, sys
import urllib.request

NL = chr(10)
ROOT = __file__.rsplit("\\Tools", 1)[0] if "\\Tools" in __file__ else "."

def read(p):
    return io.open(p, encoding="utf-8").read()

def write(p, h):
    io.open(p, "w", encoding="utf-8", newline=NL).write(h)

# ── 事實來源 ──
V = re.search(r"<Version>([^<]+)</Version>", read(ROOT + "\\XinSpect.csproj")).group(1)
minor = int(V.split(".")[1])
ROMAN = [(100,"C"),(90,"XC"),(50,"L"),(40,"XL"),(10,"X"),(9,"IX"),(5,"V"),(4,"IV"),(1,"I")]
r = minor
roman = ""
for val, sym in ROMAN:
    while r >= val:
        roman += sym; r -= val
UL = "ULTIMA " + roman

entries = json.load(open(ROOT + "\\site\\changelog.json", encoding="utf-8"))
DATE = entries[0]["date"] or ""
N = len(entries)

# GitHub latest release 實況（含資產大小；失敗則沿用頁面現值）
gh = {}
try:
    req = urllib.request.Request(
        "https://api.github.com/repos/Xinglanclever/XinSpect/releases/latest",
        headers={"User-Agent": "xs-site-sync"})
    rel = json.load(urllib.request.urlopen(req, timeout=30))
    if rel.get("tag_name") == "v" + V:
        gh = {a["name"]: format(a["size"], ",") for a in rel.get("assets", [])}
except Exception as e:
    sys.stderr.write("GitHub API 不可用，資產表沿用現值：" + str(e) + "\n")

# ── 替換表（zh+EN 共通；逐頁再補各自措辭）──
def subs_for(h):
    s = []
    # 版本字串（所有出現處）
    for old in ("v3.28 ULTIMA XXVIII", "v3.31 ULTIMA XXXI"):
        pass  # 舊版號不寫死：以下用正則處理
    return s

def bump(h):
    changes = 0
    # 版號字串（不限舊版號：任何 vX.Y ULTIMA 皆更新）
    h2, n = re.subn(r"v\d+\.\d+ ULTIMA [A-Z]+", "v" + V + " " + UL, h)
    changes += n
    # 導覽/hero 下載字樣
    h2, n = re.subn(r"(releases/latest/download/XinSpectDeploy\.exe\">)[^<]*(下載|Get) v\d+\.\d+(</a>)",
                    r"\g<1>\g<2> v" + V + r"\g<3>", h2)
    changes += n
    # xsc 框與 aria
    h2, n = re.subn(r"(<b>)v\d+\.\d+(</b>)", r"\g<1>v" + V + r"\g<2>", h2)
    changes += n
    h2, n = re.subn(r"(不連網，v)\d+\.\d+(，)", r"\g<1>" + V + r"\g<2>", h2)
    changes += n
    h2, n = re.subn(r"(no network, v)\d+\.\d+(, quick-start)", r"\g<1>" + V + r"\g<2>", h2)
    changes += n
    # 下載節區 note（日期取 changelog 最新筆）
    h2, n = re.subn(r"(下載</h2><span class=\"note\">v)\d+\.\d+( ・ ULTIMA [A-Z]+ ・ )\d{4}-\d{2}-\d{2}",
                    r"\g<1>" + V + r"\g<2>" + DATE, h2)
    changes += n
    # EN 下載節區 note
    h2, n = re.subn(r"(Download</h2><span class=\"note\">v)\d+\.\d+( ・ ULTIMA [A-Z]+ ・ )\d{4}-\d{2}-\d{2}",
                    r"\g<1>" + V + r"\g<2>" + DATE, h2)
    changes += n
    # JSON-LD
    h2, n = re.subn(r'("softwareVersion": ")\d+\.\d+(")', r"\g<1>" + V + r"\g<2>", h2)
    changes += n
    h2, n = re.subn(r'("dateModified": ")\d{4}-\d{2}-\d{2}(")', r"\g<1>" + DATE + r"\g<2>", h2)
    changes += n
    # 徽章 base 與 tag 比較
    h2, n = re.subn(r"(el\.textContent=)|('GitHub 最新 ')", r"\g<1>\g<2>", h2)  # 佔位避免誤換
    h2, n = re.subn(r"(if\(tag===')\d+\.\d+('\))", r"\g<1>" + V + r"\g<2>", h2)
    changes += n
    h2, n = re.subn(r"(GitHub 最新 '\+r\.tag_name\+' ・ v)\d+\.\d+( 資產待建置)", r"\g<1>" + V + r"\g<2>", h2)
    changes += n
    # 統計帶沿革筆數
    h2, n = re.subn(r'(<div class="n"><i>)\d+(</i></div><div class="l">(?:筆沿革全程可考|release notes, fully traceable))',
                    lambda m: m.group(1) + str(N) + m.group(2), h2)
    changes += n
    h2, n = re.subn(r"(\d+) 筆全程可考", str(N) + " 筆全程可考", h2)
    changes += n
    h2, n = re.subn(r"(\d+) entries, fully traceable", str(N) + " entries, fully traceable", h2)
    changes += n
    # 資產表大小（GitHub 實況可用時逐列覆寫）
    if gh:
        for name, size in gh.items():
            pat = "<td>" + re.escape(name) + "</td><td>[0-9,]+ bytes</td>"
            rep = "<td>" + name + "</td><td>" + size + " bytes</td>"
            h2, n = re.subn(pat, rep, h2)
            changes += n
        h2, n = re.subn(r"(版本變體對照 ・ v)\d+\.\d+( 實發大小)", r"\g<1>" + V + r"\g<2>", h2)
        changes += n
        h2, n = re.subn(r"(Release variants ・ v)\d+\.\d+( actual sizes)", r"\g<1>" + V + r"\g<2>", h2)
        changes += n
    return h2, changes

changed_total = 0
for page in ("site/index.html", "site/index-en.html"):
    h = read(page)
    h2, c = bump(h)
    if c:
        write(page, h2)
    print(page, "changes:", c)
    changed_total += c

# 辭典按鈕鍵數（若 factkeys.json 有變）
try:
    fk = json.load(open(ROOT + "\\site\\factkeys.json", encoding="utf-8"))
    total = fk["total"]
    for page in ("site/index.html", "site/index-en.html"):
        h = read(page)
        h2, n = re.subn(r"(搜全部 |Search all )[\d,]+( 把鍵| keys)", r"\g<1>" + format(total, ",") + r"\g<2>", h)
        if n:
            write(page, h2)
            print(page, "辭典按鈕鍵數更新 ×", n)
            changed_total += n
except Exception as e:
    sys.stderr.write("factkeys 按鈕更新略過：" + str(e) + "\n")

print("TOTAL changes:", changed_total)
