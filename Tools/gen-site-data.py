# 官網資料生成器：從 Nav/ChangelogCatalog.cs 全量抽出沿革，
# 產出 site/changelog.json（全量）與 site/feed.xml（最新 20 筆 RSS）。
# 用法：python -I Tools/gen-site-data.py
import json, re
from pathlib import Path
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Nav" / "ChangelogCatalog.cs"
SITE = ROOT / "site"

text = SRC.read_text(encoding="utf-8-sig")
entries = []
for b in re.split(r"new ChangeEntry", text)[1:]:
    v = re.search(r'Version\s*=\s*"([^"]+)"', b)
    d = re.search(r'Date\s*=\s*"([^"]*)"', b)
    t = re.search(r'Title\s*=\s*"([^"]+)"', b)
    if v and t:
        entries.append({"v": v.group(1), "date": d.group(1) if d else "", "title": t.group(1)})

(SITE / "changelog.json").write_text(
    json.dumps(entries, ensure_ascii=False, indent=1) + "\n", encoding="utf-8", newline="\n")

latest = entries[0]
items = []
for e in entries[:20]:
    date = e["date"] or "1970-01-01"  # 進倉前階段無日期；RSS 需要合法日期，如實用佔位並在標題標註
    suffix = "" if e["date"] else "（進倉前階段，無日期紀錄）"
    items.append(
        f"    <item>\n"
        f"      <title>{escape(e['v'] + ' — ' + e['title'] + suffix)}</title>\n"
        f"      <link>https://xinspect.de5.net/#log-v{escape(e['v'])}</link>\n"
        f"      <guid isPermaLink=\"false\">xinspect-{escape(e['v'])}</guid>\n"
        f"      <pubDate>{date}T00:00:00Z</pubDate>\n"
        f"    </item>"
    )
feed = (
    '<?xml version="1.0" encoding="UTF-8"?>\n'
    '<rss version="2.0"><channel>\n'
    "  <title>曦覽 XinSpect 版本沿革</title>\n"
    "  <link>https://xinspect.de5.net/</link>\n"
    "  <description>唯讀的 Windows 硬體事實採集器——版本更新紀錄（最新 20 筆，全程見官網）</description>\n"
    "  <language>zh-Hant</language>\n"
    "  <generator>XinSpect site tools (Tools/gen-site-data.py)</generator>\n"
    f"  <lastBuildDate>{escape(latest['date'] or '1970-01-01')}T00:00:00Z</lastBuildDate>\n"
    + "\n".join(items) + "\n</channel></rss>\n"
)
(SITE / "feed.xml").write_text(feed, encoding="utf-8", newline="\n")
print(f"changelog.json: {len(entries)} entries (latest {latest['v']}); feed.xml: 20 items")

# ── site/factkeys.json：157 域 → 全部鍵名（點域片彈出全域鍵表）──
import collections
cat = (ROOT / "Services" / "FactKeyCatalog.cs").read_text(encoding="utf-8-sig")
seg = cat[cat.index("Keys { get; }"):]
import re as _re
mk = _re.search(r"=\s*\[(.*?)\];", seg, _re.S)
allkeys = _re.findall(r'"([a-z][a-z0-9]*(?:\.[a-z0-9_]+)+)"', mk.group(1))
byd = collections.defaultdict(list)
for k in sorted(set(allkeys)):
    byd[k.split(".")[0]].append(k)
doms_sorted = sorted(byd.items(), key=lambda x: (-len(x[1]), x[0]))
fk = {
    "total": len(set(allkeys)),
    "domainCount": len(doms_sorted),
    "domains": [{"d": d, "n": len(ks), "keys": ks} for d, ks in doms_sorted],
}
(SITE / "factkeys.json").write_text(
    json.dumps(fk, ensure_ascii=False, separators=(",", ":")) + chr(10),
    encoding="utf-8", newline="\n")
print(f"factkeys.json: {fk['total']} keys / {fk['domainCount']} domains")

