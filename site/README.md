# site/ — 曦覽官網

瑞士現代主義雙語站（index.html＋index-en.html＋changelog.json＋feed.xml＋og.png＋404.html＋robots/sitemap/webmanifest/_headers），部署到 Cloudflare Pages
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

逐項檢查線上（51 項）：200／404、SEO 標記、新節區（六態／事實域／誠實邊界／FAQ／安全）、部署器唯一入口（直連鏈接必須為 0）、錨點 scroll-margin、行動端語言切換、GitHub 星標、百科連結、贊助複製按鈕、
安全頭（CSP／nosniff／Referrer-Policy）、changelog.json 筆數動態等於 ChangelogCatalog.cs 的 Version 出現數、
最新版號等於 `XinSpect.csproj` 的 `<Version>`、feed.xml 20 筆且首筆日期＝最新版日期、zh／en 內聯 script 逐一 `node --check`（語法守衛，見下）。

## JS 語法守衛

```bash
python -I Tools/check-js.py obj/_v_js/zh.html   # 先把 html 複製到 obj/ 再跑
```

抽取指定 html 的內聯 `<script>` 逐一 `node --check`，印出失敗塊數。**切勿對 site/ 內的 html 直接跑**——抽取的 .js 會寫在同目錄、跟著部署出去。

## 資料生成

```bash
python -I Tools/gen-site-data.py
```

從 `Nav/ChangelogCatalog.cs` 全量生成 `site/changelog.json`（131 筆＝版號＋進倉前階段）與 `site/feed.xml`（最新 20 筆 RSS）。值守 Cron（automation-10c018e5，每 6 小時）會在版本變化時自動執行本流程並部署。

## 更新紀錄（changelog.json）

由 `Nav/ChangelogCatalog.cs` 全量抽出（131 筆＝129 版號＋2 個進倉前階段，欄位順序差異已容忍）。
每次改版號後重新生成（見上方「資料生成」）。

## 結構與規矩

- **畫風**：紅 `#E30613`／墨 `#111`／紙 `#FAFAF7`，零圓角零陰影，Helvetica；與 GUI／xsc 一致。
- **下載定位**：全站唯一入口是 XinSpectDeploy.exe（部署器）；主程式與終端機的直連鏈接**刻意為零**。
- **宣稱對帳**：1,178 靜態目錄鍵（FactKeyCatalog；含硬體、OS、安全、診斷與治理，並非硬體讀值數）、69 動態家族（FactKeyDynamicCatalog）、157 事實域、
  65 畫面——改動前先對源碼複算，不寫沒驗證過的數字。
- **版本稱號**：hero 的「ULTIMA〈羅馬數字〉」為站點自創，按小版本號推（3.16=XVI、3.26=XXVI）。
