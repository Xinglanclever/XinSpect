# 鏡像同步（Google Drive／中國直連線路）

這一頁解決的是一件事：**發一版，不該手動上傳三次。**

## 問題

GitHub Release 是一次 API 呼叫就完成（`Tools/publish_release.py`）。其餘來源沒有對應的
自動化，於是每一次發版都要開瀏覽器、拖檔案、等上傳、複製連結，做完還不確定三邊是不是
同一份檔案。版本一多，這件事就會開始漏。

## 兩種鏡像，兩種模式

安裝器的第 2 格是 **分享頁**（`page`）：它只開瀏覽器，請使用者自己按「下載」。
第 3 格是 **中國直連線路**（`direct`）：目錄型端點，安裝器自己抓檔，跟按 1 一樣有進度條、
速度與剩餘時間。

判斷哪一種，看的是**端點本身**，不是「它叫什麼」：

- 能用 `GET <目錄>/XinSpect.exe` 直接取回檔案的 → `direct`；
- 只能給一個分享頁、要人按按鈕（甚至要登入）的 → `page`，放第 2 格。

## 作法：固定資料夾，就地覆寫

每個來源都只留一個資料夾：

```
XinSpect/latest/XinSpect.exe
XinSpect/latest/BlueSquadronBridge.exe
XinSpect/latest/XinSpectDeploy.exe
XinSpect/latest/SHA256SUMS.txt
```

每版把同名檔案蓋掉，分享的是**資料夾**——所以連結從第一次之後就不再變。連帶三個好處：

1. `Installer/mirrors.txt` 不用跟著每版改。
2. 安裝器 exe 不用重編：它在執行時抓 `mirrors.txt`（`raw.githubusercontent.com`），不是編進去的。
3. 「同步」變成一次指令，而且多個來源**同時**上傳（各自一個執行緒），不是一個等一個。

## 一次性設定

大約十分鐘，做一次就好。

1. **裝 rclone**：[rclone.org/downloads](https://rclone.org/downloads/)。也可以只把
   `rclone.exe` 解到 `Tools/bin/`——`sync-mirrors.py` 會優先找這個位置，不必進 PATH。

2. **建 remote**（`rclone config`）：

   | provider | remote | 類型 | 說明 |
   |---|---|---|---|
   | `gdrive` | `gdrive:` | Google Drive | 第一次要瀏覽器授權一次；分享資料夾，`page` |
   | `chinadirect` | 自訂 | sftp／webdav／s3…… | 直連線路的落點，`direct` |

3. **建資料夾並分享一次**（只有 `page` 這一格需要分享）：Drive 開 `XinSpect/latest/`，
   按分享、複製資料夾連結（`/drive/folders/<id>`）。直連線路不需要分享頁——它的網址
   本身就是目錄。

4. **填 `Tools/mirrors.json`**：`enabled` 改 `true`，`share` 貼上目錄或分享連結
   （`direct` 的結尾要有斜線），`mode` 照上面。

5. **第一次同步**：

   ```
   python Tools/sync-mirrors.py --sync --update-mirrors
   ```

   它會把四件資產上傳、逐檔對帳，並把對應的行寫進 `Installer/mirrors.txt`。把這個檔一起提交。

## 每一版要做的事

```
python Tools/sync-mirrors.py --sync
```

（版號預設讀 `XinSpect.csproj`；不在發版機器上就加 `--source release --version 3.08`，
它會直接從 GitHub Release 抓四件資產再上傳。）

跑完它會逐檔印出「一致」或「不一致」，退出碼 0／1。連結沒變的話，
`mirrors.txt` 一個字都不用改。

## 對帳

```
python Tools/sync-mirrors.py --check
```

任何時候都可以跑。它比對遠端與本地四件資產的大小與雜湊：

- 能算 SHA-256 的：比雜湊。
- 只回 MD5／QuickXor 的（Google Drive）：比大小，並**明講**「只有大小可比」——
  不把「大聲宣布一致」和「只驗了一部分」寫成同一句話。

## CI（可選）

`.github/workflows/mirror.yml` 在 Release 發佈後自動跑同一支腳本。要啟用它，加一個
repository secret `RCLONE_CONF`（`rclone config file` 那份 `rclone.conf` 的 base64），
沒設就整個 job 跳過，不會紅。

## 這一頁的界線

- **分享頁能不能真的下載，只有人點一次才知道。** 腳本能確認檔案上去了、大小與雜湊對得上，
  不能確認「這個分享連結對陌生人可用」。`Installer/mirrors.txt` 的第一條規矩就是為此：
  只列自己點過的連結。
- Drive 沒有 SHA-256，對帳只到大小。
- 這一頁的自動化建立在 rclone 上；rclone 壞了就是換 rclone，不是改安裝器。
