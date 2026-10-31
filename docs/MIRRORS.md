# 鏡像同步（Google Drive／123 雲盤）

這一頁解決的是一件事：**發一版，不該手動上傳三次。**

## 問題

GitHub Release 是一次 API 呼叫就完成（`Tools/publish_release.py`）。Drive 與 123 雲盤沒有
對應的自動化，於是每一次發版都要開瀏覽器、拖檔案、等上傳、複製連結——兩個網盤不能同時做，
做完還不確定三邊是不是同一份檔案。版本一多，這件事就會開始漏。

## 作法：固定資料夾，就地覆寫

兩個網盤都只留一個資料夾：

```
XinSpect/latest/XinSpect.exe
XinSpect/latest/BlueSquadronBridge.exe
XinSpect/latest/XinSpectDeploy.exe
XinSpect/latest/SHA256SUMS.txt
```

每版把同名檔案蓋掉，分享的是**資料夾**——所以分享連結從第一次之後就不再變。連帶三個好處：

1. `Installer/mirrors.txt` 不用跟著每版改。
2. 安裝器 exe 不用重編：它在執行時抓 `mirrors.txt`（`raw.githubusercontent.com`），不是編進去的。
3. 「同步」變成一次指令，而且兩個網盤**同時**上傳（各自一個執行緒），不是一個等一個。

## 一次性設定

大約十分鐘，做一次就好。

1. **裝 rclone**：[rclone.org/downloads](https://rclone.org/downloads/)。也可以只把
   `rclone.exe` 解到 `Tools/bin/`——`sync-mirrors.py` 會優先找這個位置，不必進 PATH。

2. **建兩個 remote**（`rclone config`）：

   | remote | 類型 | 說明 |
   |---|---|---|
   | `gdrive` | Google Drive | 第一次要瀏覽器授權一次 |
   | `123` | WebDAV | 123 雲盤沒有 rclone 原生後端。帳號有 WebDAV 就直接用；沒有就架一個 AList（它也講 WebDAV），把 123 雲盤掛進去 |

3. **建資料夾並分享一次**：在兩個網盤各開 `XinSpect/latest/`，隨意放一個檔案，按分享，
   複製連結。Drive 是資料夾連結（`/drive/folders/<id>`），123 雲盤是分享頁
   （`/s/<id>`）。兩者都不支援「目錄型直連」——Drive 沒有這種端點，123 的直連會過期，
   所以 `mirrors.txt` 的兩格都是 `page` 模式：安裝器開瀏覽器、請使用者按「下載」。

4. **填 `Tools/mirrors.json`**：把兩個 provider 的 `enabled` 改成 `true`，`share` 貼上第 3 步的連結。

5. **第一次同步**：

   ```
   python Tools/sync-mirrors.py --sync --update-mirrors
   ```

   它會把四件資產上傳、逐檔對帳，並把兩行寫進 `Installer/mirrors.txt`。把這個檔一起提交。

## 每一版要做的事

```
python Tools/sync-mirrors.py --sync
```

（版號預設讀 `XinSpect.csproj`；不在發版機器上就加 `--source release --version 3.08`，
它會直接從 GitHub Release 抓四件資產再上傳。）

跑完它會逐檔印出「一致」或「不一致」，退出碼 0／1。分享連結沒變的話，
`mirrors.txt` 一個字都不用改。

## 對帳

```
python Tools/sync-mirrors.py --check
```

任何時候都可以跑。它比對遠端與本地四件資產的大小與雜湊：

- 有 SHA-256 的網盤：比雜湊。
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
- 網盤會改自己的網頁與 API；這裡的自動化建立在 rclone 上，rclone 壞了就是換 rclone，
  不是改安裝器。
