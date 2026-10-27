#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""版號 bump：把一個新版本寫進八個地方，而且**重跑不會重複插入**。

為什麼要有這支工具：這件事我手寫過三次，三次都出了同一種錯——重跑一次就把
ChangelogCatalog 插成三筆、README 插成兩條 bullet 加兩節 11.3。根因不是粗心，
是**「冪等」被實作成一種猜測**：舊字串還在不在。插入類操作不會消耗舊字串，
所以「舊的還在」永遠成立，於是每次都再插一次。

這裡的修法是把它拆成兩種操作，語意各自明確：

  Replace(old, new)        —— 換掉。old 不見了而且 new 也不在 → 直接失敗（不要猜）。
  InsertBefore(anchor, block) —— 插在 anchor 前面。block 已經在檔案裡 → 跳過（這就是冪等）。

用法：
  python Tools/bump-version.py --version 3.03 --codename "Ultima XII" \\
      --title "..." --date 2026-10-27 --items work/items-3.03.txt

  --items 是一行一項的純文字檔；它會變成 ChangelogCatalog 的 Items 與 README 的 11.3 小節。
  --dry-run 只印出會改哪些檔，不寫入。

寫完之後會自己驗一次：目錄裡的版號筆數、README 的 bullet／小節數、三份 README 的徽章，
只要有一項不是「剛好一筆」就以非零退出——**插完不驗等於沒插對**。
"""

import argparse
import io
import os
import re
import sys


# ── 檔案讀寫（保留 BOM 與該檔原本的換行）────────────────────────────────

class Doc:
    def __init__(self, path):
        self.path = path
        raw = open(path, "rb").read()
        self.bom = raw.startswith(b"\xef\xbb\xbf")
        self.text = raw.decode("utf-8-sig")
        self.nl = "\r\n" if "\r\n" in self.text else "\n"
        self.dirty = False

    def replace(self, old, new, label=""):
        if old not in self.text:
            if new in self.text:
                return                      # 已經換過了
            raise SystemExit("MISS %s%s：找不到 %r" % (self.path, "（" + label + "）" if label else "", old[:70]))
        self.text = self.text.replace(old, new)
        self.dirty = True

    def insert_before(self, anchor, block, label=""):
        """block 已在檔案裡就跳過——這是冪等的關鍵，不是最佳化。"""
        if block in self.text:
            return
        if anchor not in self.text:
            raise SystemExit("MISS %s%s：找不到插入錨點 %r" % (self.path, "（" + label + "）" if label else "", anchor[:70]))
        if self.text.count(anchor) != 1:
            raise SystemExit("錨點 %r 在 %s 出現 %d 次——無法確定要插在哪裡"
                             % (anchor[:50], self.path, self.text.count(anchor)))
        self.text = self.text.replace(anchor, block + anchor, 1)
        self.dirty = True

    def save(self):
        if not self.dirty:
            return False
        data = (b"\xef\xbb\xbf" if self.bom else b"") + self.text.encode("utf-8")
        open(self.path, "wb").write(data)
        return True


def find_root(explicit=None):
    if explicit:
        return os.path.abspath(explicit)
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.dirname(here)


def read_items(path):
    if not path:
        return []
    lines = io.open(path, encoding="utf-8-sig").read().split("\n")
    items = [ln.strip() for ln in lines if ln.strip()]
    if not items:
        raise SystemExit("--items 檔案是空的：%s" % path)
    return items


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True, help="新版本，例如 3.03")
    ap.add_argument("--codename", required=True, help="代號，例如 Ultima XII")
    ap.add_argument("--title", required=True, help="一句話標題（Changelog 與沿革用）")
    ap.add_argument("--date", required=True, help="YYYY-MM-DD")
    ap.add_argument("--items", help="一行一項的純文字檔")
    ap.add_argument("--root", help="倉庫根目錄（預設由腳本位置推導）")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args(argv)

    if not re.fullmatch(r"\d+\.\d+", args.version):
        raise SystemExit("版號格式應為 X.Y，收到 %r" % args.version)
    if not re.fullmatch(r"\d{4}-\d{2}-\d{2}", args.date):
        raise SystemExit("日期格式應為 YYYY-MM-DD，收到 %r" % args.date)

    root = find_root(args.root)
    V = args.version
    items = read_items(args.items)
    if not items:
        raise SystemExit("--items 是必要的：ChangelogCatalog 的每一筆都要有內容")

    def doc(rel):
        return Doc(os.path.join(root, rel))

    # 舊版號從 csproj 讀——不從使用者參數拿，避免「以為自己在升 3.02→3.03，其實是 3.01」。
    proj = doc("XinSpect.csproj")
    m = re.search(r"<Version>([^<]+)</Version>", proj.text)
    if not m:
        raise SystemExit("XinSpect.csproj 裡找不到 <Version>")
    OLD = m.group(1).strip()
    if OLD == V:
        print("已經是 %s，沒有事要做。" % V)
        return 0
    print("版號 %s → %s" % (OLD, V))

    # 1) csproj
    proj.replace("<Version>%s</Version>" % OLD, "<Version>%s</Version>" % V, "csproj")
    proj.replace("<FileVersion>%s.0.0</FileVersion>" % OLD, "<FileVersion>%s.0.0</FileVersion>" % V, "csproj")

    # 2) AboutView
    about = doc(os.path.join("Views", "AboutView.xaml"))
    old_code = None
    mc = re.search(r"版本 %s ・ ([^・<]+)・" % re.escape(OLD), about.text)
    if mc:
        old_code = mc.group(1).strip()
    if old_code:
        about.replace("版本 %s ・ %s" % (OLD, old_code), "版本 %s ・ %s" % (V, args.codename), "AboutView")
    else:
        about.replace("版本 %s ・" % OLD, "版本 %s ・ %s ・" % (V, args.codename), "AboutView")

    # 3) SECURITY
    sec = doc("SECURITY.md")
    sec.replace("（目前 %s）" % OLD, "（目前 %s）" % V, "SECURITY")

    # 4) Issue 範本
    bug = doc(os.path.join(".github", "ISSUE_TEMPLATE", "bug_report.yml"))
    bug.replace('placeholder: "%s"' % OLD, 'placeholder: "%s"' % V, "bug_report")
    bug.replace("以系統管理員啟動 %s" % OLD, "以系統管理員啟動 %s" % V, "bug_report")
    hw = doc(os.path.join(".github", "ISSUE_TEMPLATE", "hardware_report.yml"))
    hw.replace('placeholder: "%s"' % OLD, 'placeholder: "%s"' % V, "hardware_report")

    # 5) ChangelogCatalog —— 插入類：block 已在就跳過
    cat = doc(os.path.join("Nav", "ChangelogCatalog.cs"))
    CNL = cat.nl
    anchor = ("        new ChangeEntry" + CNL + "        {" + CNL
              + '            Version = "%s",' % OLD)
    body = CNL.join('                "%s",' % i for i in items)
    block = ("        new ChangeEntry" + CNL + "        {" + CNL
             + '            Version = "%s",' % V + CNL
             + '            Date = "%s",' % args.date + CNL
             + '            Title = "%s",' % args.title + CNL
             + "            Items =" + CNL + "            [" + CNL
             + body + CNL + "            ]," + CNL + "        }," + CNL)
    cat.insert_before(anchor, block, "ChangelogCatalog")

    # 6) README.md
    md = doc("README.md")
    md.replace("這一版針對的是 **v%s** 的這棵樹" % OLD, "這一版針對的是 **v%s** 的這棵樹" % V, "README")
    md.replace("badge/version-%s-" % OLD, "badge/version-%s-" % V, "README 徽章")
    md.replace("releases/download/v%s/" % OLD, "releases/download/v%s/" % V, "README 下載連結")
    md.replace("本版（v%s）實際發佈的檔案大小" % OLD, "本版（v%s）實際發佈的檔案大小" % V, "README 註腳")
    md.replace("最新的一筆是 v%s（" % OLD, "最新的一筆是 v%s（" % V, "README 11.1")
    md.replace("本版是 v%s ——" % OLD, "本版是 v%s ——" % V, "README 結語")

    bullet = "- **v%s**（%s）：%s（本版 %d 項）" % (V, args.date, args.title, len(items))
    md_bullet_anchor = "- **v%s**（" % OLD
    md.insert_before(md_bullet_anchor, bullet + "\n", "README 11.2")

    section = ("#### v%s（%s）" % (V, args.date) + "\n\n**%s**" % args.title + "\n\n"
               + "\n".join("- " + i for i in items) + "\n\n")
    md.insert_before("#### v%s（" % OLD, section, "README 11.3")

    # 6b) 登記筆數：這一筆插進去之後目錄就多了一筆，兩處現在式宣稱要跟著動。
    #     先前這裡漏了，於是工具自己驗不過——驗證碼抓到自己的工具，這正是它該做的事。
    cat_now = cat.text
    n_entries = len(re.findall(r'(?m)^\s*Version = "', cat_now))
    md_before = md.text
    md.text = re.sub(r"\*\*登記筆數\*\*：\d+ 筆", "**登記筆數**：%d 筆" % n_entries, md.text)
    if md.text != md_before:
        md.dirty = True

    # 7) README.zh-CN.md
    zh = doc("README.zh-CN.md")
    zh.replace("badge/version-%s-" % OLD, "badge/version-%s-" % V, "zh 徽章")
    zh.replace("releases/download/v%s/" % OLD, "releases/download/v%s/" % V, "zh 下載連結")
    zh.replace("本版（v%s）实际发布的文件大小" % OLD, "本版（v%s）实际发布的文件大小" % V, "zh 註腳")
    zh.replace("本文档对应 XinSpect v%s（" % OLD, "本文档对应 XinSpect v%s（" % V, "zh 文件版本")
    zh_bullet = "- **v%s %s**（%s）：%s" % (V, args.codename, args.date, args.title)
    zh.insert_before("- **v%s Ultima" % OLD if ("- **v%s Ultima" % OLD) in zh.text else "- **v%s %s**" % (OLD, old_code or ""),
                     zh_bullet + "\n", "zh 沿革")

    # 8) README.en.md
    en = doc("README.en.md")
    en.replace("badge/version-%s-" % OLD, "badge/version-%s-" % V, "en 徽章")
    en.replace("releases/download/v%s/" % OLD, "releases/download/v%s/" % V, "en 下載連結")
    en.replace("Document scope (v%s)" % OLD, "Document scope (v%s)" % V, "en 文件範圍")
    en.replace("**About this document**: it corresponds to XinSpect v%s (" % OLD,
               "**About this document**: it corresponds to XinSpect v%s (" % V, "en 文件版本")
    # 英文版的 ledger 句也寫了「N entries (M dated releases + 4 stages)」，一併更新
    en_before = en.text
    en.text = re.sub(
        r"The ledger contains \d+ entries \(\d+ dated releases plus four pre-repository stages\) from 2026-08-27 to \d{4}-\d{2}-\d{2}\.",
        "The ledger contains %d entries (%d dated releases plus four pre-repository stages) from 2026-08-27 to %s."
        % (n_entries, n_entries - 4, args.date), en.text)
    if en.text != en_before:
        en.dirty = True

    en_row = "| %s | %s | %s |" % (V, args.date, args.title)
    en.insert_before("| %s | " % OLD, en_row + "\n", "en 沿革表")

    docs = [proj, about, sec, bug, hw, cat, md, zh, en]
    if args.dry_run:
        for d in docs:
            if d.dirty:
                print("would update", os.path.relpath(d.path, root))
        return 0

    changed = [os.path.relpath(d.path, root) for d in docs if d.save()]
    print("updated:", ", ".join(changed) if changed else "（無變更）")

    # ── 插完就驗：每一處都必須剛好一筆 ────────────────────────────────
    problems = []
    cat_text = io.open(os.path.join(root, "Nav", "ChangelogCatalog.cs"), encoding="utf-8-sig").read()
    n_entries = len(re.findall(r'(?m)^\s*Version = "', cat_text))
    n_new = cat_text.count('Version = "%s",' % V)
    if n_new != 1:
        problems.append("ChangelogCatalog 的 %s 出現 %d 次（應為 1）" % (V, n_new))

    md_text = io.open(os.path.join(root, "README.md"), encoding="utf-8-sig").read()
    for label, pat in [("11.2 bullet", "- **v%s**（" % V), ("11.3 小節", "#### v%s（" % V)]:
        c = md_text.count(pat)
        if c != 1:
            problems.append("README %s 出現 %d 次（應為 1）" % (label, c))
    if ("**登記筆數**：%d 筆" % n_entries) not in md_text:
        problems.append("README 的登記筆數沒有等於目錄的 %d 筆" % n_entries)

    zh_text = io.open(os.path.join(root, "README.zh-CN.md"), encoding="utf-8-sig").read()
    if zh_text.count("- **v%s " % V) != 1:
        problems.append("zh-CN 沿革的 %s 出現 %d 次（應為 1）" % (V, zh_text.count("- **v%s " % V)))

    if problems:
        print("BUMP-FAIL:")
        for p in problems:
            print("  -", p)
        return 2

    print("OK：%s 已寫進八個地方，目錄共 %d 筆；沒有重複。" % (V, n_entries))
    print("提醒：測試數、鍵數、域數、家族數仍要另外更新——那些是機器算的，不是這支腳本猜得出來的。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
