# site/ — 曦覽官網

瑞士現代主義一頁站（index.html + changelog.json + og.png + 404.html），部署到 Cloudflare Pages
專案 `xinspect-web`，自訂網域 https://xinspect.de5.net（技術備援 xinspect-web.pages.dev）。

## 部署

```bash
cd /c/Users/Administrator/XinSpect
npx wrangler pages deploy site --project-name xinspect-web --branch main --commit-dirty=true
```

wrangler OAuth 憑證在本機；`_headers` 會一併上傳（nosniff／Referrer-Policy／Permissions-Policy＋靜態資產快取分級）。

## 驗證

```bash
bash Tools/verify-site.sh
```

逐項檢查線上：200／404、SEO 標記、新節區、部署器唯一入口（直連鏈接必須為 0）、安全頭、
changelog.json 筆數（130）與最新版號等於 `XinSpect.csproj` 的 `<Version>`。

## 更新紀錄（changelog.json）

由 `Nav/ChangelogCatalog.cs` 全量抽出（130 筆＝128 版號＋2 個進倉前階段，欄位順序差異已容忍）。
每次改版號後重新生成：

```bash
python -I <gen_changelog.py>   # 腳本見當次會話工作區；抽法：逐 ChangeEntry 區塊抓 Version/Date/Title
```

## 結構與規矩

- **畫風**：紅 `#E30613`／墨 `#111`／紙 `#FAFAF7`，零圓角零陰影，Helvetica；與 GUI／xsc 一致。
- **下載定位**：全站唯一入口是 XinSpectDeploy.exe（部署器）；主程式與終端機的直連鏈接**刻意為零**。
- **宣稱對帳**：1,178 靜態鍵（FactKeyCatalog）、69 動態家族（FactKeyDynamicCatalog）、157 事實域、
  65 畫面——改動前先對源碼複算，不寫沒驗證過的數字。
- **版本稱號**：hero 的「ULTIMA〈羅馬數字〉」為站點自創，按小版本號推（3.16=XVI、3.25=XXV）。
