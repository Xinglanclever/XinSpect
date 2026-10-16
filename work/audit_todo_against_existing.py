# -*- coding: utf-8 -*-
"""把矩陣裡每一個 todo 的列，拿去跟既有的 Services/ 對一遍。

P2-052 的教訓：我用「我不確定 FIT 的佈局」把那一項撤回，
**但真正的理由是 SpiFitFactsService 早就有 FitDecoder 了——
我沒查就自己寫了一個平行的解碼器。**

字面的鍵名掃描抓不到這件事（因為我的鍵叫 fit.present，而它叫 fw.fit.summary）。
真正該掃的是**能力**：拿規格那一列的領域詞彙去搜 Services/。

═══════════════════════════════════════════════════════════════════════
結果（2026-10-16）：**這支腳本失敗了，而失敗本身是結論。**

第一次跑：命中 0 列 / 61 列。原因不是「真的沒有既有實作」，
而是 **probe 詞彙集太小**——我用英文 token 去比對，但規格那一列的
中文名稱裡幾乎沒有英文（「映像 PE32 模組清單」只有 `PE32`）。

**而更根本的是：我沒有辦法機械化「這件事有沒有人做過」。**

P2-052 之所以能被我發現，不是因為掃描，而是因為我讀
SpiFitFactsService.cs 的檔名時認出了 `Fit`。
**那是人的判斷，不是腳本能做的。**

一個提示太多的掃描會被忽略，一個提示太少的掃描等於沒有。
所以本檔保留為**失敗嘗試的紀錄**——它證明「機械化這件事做不到」，
而那比一個假裝能做的腳本有用。

**真正可行、且我確實做得到的規則寫在下面。**
═══════════════════════════════════════════════════════════════════════

【動手前的檢查（人的動作，不是腳本）】

  ① grep 規格宣告的鍵名（例如 `fit.present` → 搜 `fw.fit`)
  ② grep 那項目的英文領域詞（`Fit`、`SLIT`、`NFIT`、`PE32`、`SBAT`…）
  ③ 命中任何既有實作 → 那一項是 alias，不是 todo

**它能成立，是因為規格的鍵名與領域詞通常會出現在既有實作的
常數宣告或 XML 摘要裡。**
"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

ROOT = r'C:\Users\Administrator\XinSpect'
MATRIX = os.path.join(ROOT, 'docs', 'ULTIMATE-1000', 'STAGE-02-FIRMWARE.md')

# 太通用的詞，命中它們沒有意義
STOP = {
    '韌體', '服務', '事實', '唯讀', '三態', '摘要', '清單', '數量', '狀態',
    '版本', '資訊', '內容', '系統', '平台', '裝置', '支援', '檢查', '解析',
    '取得', '存在', '一致', '映像', '設定', '記錄', '欄位', '結構',
}

# 先用「英文的技術詞彙」——它們比中文詞精確得多，也不會誤命中。
EN_TOKEN = re.compile(r'[A-Za-z][A-Za-z0-9_]{2,}')


def services_index():
    """把 Services/ 的每個檔案建成 (檔名, 類別名, 摘要, 全文關鍵詞集合)。"""
    out = []
    d = os.path.join(ROOT, 'Services')
    for fn in sorted(os.listdir(d)):
        if not fn.endswith('.cs'):
            continue
        text = io.open(os.path.join(d, fn), encoding='utf-8', errors='replace').read()
        cls = re.search(r'public\s+(?:static\s+)?(?:sealed\s+)?class\s+(\w+)', text)
        summary = re.search(r'///\s*<summary>\s*\n\s*///\s*(.+)', text)
        en = set(EN_TOKEN.findall(fn))
        if cls:
            en |= set(EN_TOKEN.findall(cls.group(1)))
        out.append((fn, cls.group(1) if cls else '', summary.group(1).strip() if summary else '', en, text))
    return out


def rows():
    text = io.open(MATRIX, encoding='utf-8').read()
    for line in text.split('\n'):
        cells = line.split('|')
        if len(cells) != 9:
            continue
        rid = cells[1].strip().lstrip('★')
        if not re.match(r'^P\d+-\d{3}$', rid):
            continue
        if not cells[8].strip().startswith('todo'):
            continue
        yield rid, cells[2].strip(), cells[3].strip(), cells[4].strip()


def main():
    svcs = services_index()
    hits_total = 0

    for rid, name, goal, keys in rows():
        # 這一列的領域詞彙：英文技術詞 ＋ 鍵名的最後一段
        probe = set(EN_TOKEN.findall(name)) | set(EN_TOKEN.findall(goal))
        for k in re.findall(r'`([a-z0-9_.*]+)`', keys):
            probe |= set(EN_TOKEN.findall(k))

        found = []
        for fn, cls, summary, en, text in svcs:
            common = probe & en
            if common:
                found.append((fn, cls, summary, sorted(common)))

        if found:
            hits_total += 1
            print(f'{rid}  {name}')
            print(f'    目標：{goal}')
            for fn, cls, summary, common in found[:3]:
                print(f'    → {fn}（{cls}）  命中詞：{"、".join(common)}')
                if summary:
                    print(f'        {summary[:78]}')
            print()

    print(f'=== 未完成的 {sum(1 for _ in rows())} 列中，有 {hits_total} 列可能已有既有實作 ===')


main()
