# 內嵌資料檔（Data）

這個目錄的檔案會以 `<EmbeddedResource>` 編進 `XinSpect.Decoders.dll`，隨單一執行檔分發。
**每一個檔案都必須在這裡寫明：來源、抓取日期、授權、以及「只抽了哪幾欄」。**

## `pci-ids.tsv.gz`

| 項目 | 內容 |
|---|---|
| 來源 | PCI ID Project 的 `pci.ids`（<https://pci-ids.ucw.cz/>） |
| 取得路徑 | `https://cdn.jsdelivr.net/gh/vcrhonek/hwdata@master/pci.ids` |
| 上游版本 | **2026.10.01**（檔頭 `Version:` 欄） |
| 抓取日期 | 2026-10-27 |
| 授權 | 上游檔頭自述「可在 GPL-2.0-or-later **或** 3-clause BSD 之下散布」；本專案採 **BSD-3-Clause**，因此不使本專案的 AGPL-3.0-or-later 擴散 |
| 內容 | 只抽**廠商段**與**裝置段**：2,519 個廠商、21,619 個裝置 |
| 丟掉了什麼 | 子系統（subsystem）、類別碼段、所有註解與空白 |
| 格式 | 純文字 TSV 再 gzip：`V<TAB>廠商4hex<TAB>名稱` 與 `D<TAB>廠商4hex<TAB>裝置4hex<TAB>名稱` |

### 為什麼是「抽兩段」而不是整份照收

整份 `pci.ids` 是 1.67 MB（43,261 行，含 18,174 條子系統與 802 行註解）。
本專案只需要「廠商名」與「裝置名」兩件事；子系統比對要讀設定空間 0x2C，
而本版沒有收錄子系統表，**所以刻意不猜**。抽完之後 gzip 是 219 KB。

### 重生成方式（下次換快照時）

```bash
curl -s -o pci.ids https://cdn.jsdelivr.net/gh/vcrhonek/hwdata@master/pci.ids
python - <<'PY'
import re, gzip
vend, dev, cur = [], [], None
for line in open('pci.ids', encoding='utf-8'):
    if not line.strip() or line.startswith('#'): continue
    if line.startswith('\t\t'): continue
    if line.startswith('\t'):
        m = re.match(r'^\t([0-9a-f]{4})\s+(.+)$', line.rstrip('\n'))
        if m and cur is not None: dev.append((cur, int(m.group(1), 16), m.group(2).strip()))
        continue
    m = re.match(r'^([0-9a-f]{4})\s+(.+)$', line.rstrip('\n'))
    if m: cur = int(m.group(1), 16); vend.append((cur, m.group(2).strip()))
rows = [f'V\t{v:04x}\t{n}' for v, n in vend] + [f'D\t{v:04x}\t{d:04x}\t{n}' for v, d, n in dev]
open('pci-ids.tsv.gz', 'wb').write(gzip.compress(('\n'.join(rows) + '\n').encode('utf-8'), 9))
print(len(vend), len(dev))
PY
```

換完之後**必須**同步三處，否則守門會紅：

1. `PciIdDatabase.SnapshotVersion`（版本字串）；
2. `THIRD-PARTY-NOTICES.md` 的版本欄；
3. 本檔的「上游版本」與「抓取日期」列。

`Tests/PciIdDatabaseTests` 會驗資料真的載得進來（廠商 > 2,400、裝置 > 20,000），
但**不會**驗版本字串與檔案內容一致——那個沒有機器可驗的來源，只能靠上面這三步。
