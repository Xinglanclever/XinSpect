#!/usr/bin/env bash
# 官網線上驗證：每次改完 site/ 部署後跑一遍，逐項對帳。
# 用法：bash Tools/verify-site.sh [https://xinspect.de5.net]
set -u
BASE=${1:-https://xinspect.de5.net}
fail=0
chk(){ if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1（期望 $2 實得 $3）"; fail=1; fi; }
has(){ if [ "$3" -ge "$2" ]; then echo "ok   $1"; else echo "FAIL $1（至少 $2 實得 $3）"; fail=1; fi; }
code(){ curl -s -o /dev/null -w "%{http_code}" "$1"; }
H=$(curl -s "$BASE/?v=verify-$RANDOM$RANDOM")

chk "首頁 200"                              200 "$(code "$BASE/")"
chk "robots.txt 200"                        200 "$(code "$BASE/robots.txt")"
chk "sitemap.xml 200"                       200 "$(code "$BASE/sitemap.xml")"
chk "og.png 200"                            200 "$(code "$BASE/og.png")"
chk "webmanifest 200"                       200 "$(code "$BASE/site.webmanifest")"
chk "不存在路徑回 404"                       404 "$(code "$BASE/no-such-page-verify")"
chk "og:title 在頁上"                        1   "$(echo "$H" | grep -c 'og:title')"
chk "JSON-LD（SoftwareApplication＋FAQPage）" 2  "$(echo "$H" | grep -c 'application/ld+json')"
chk "統計帶在頁上"                           1   "$(echo "$H" | grep -c 'class="stats"')"
chk "FAQ 節在頁上"                          1   "$(echo "$H" | grep -c 'id="faq"')"
chk "安全節在頁上"                          1   "$(echo "$H" | grep -c 'id="security"')"
has "changelog 篩選框在頁上"                 1   "$(echo "$H" | grep -c 'log-q')"
has "移動端選單在頁上"                       1   "$(echo "$H" | grep -c 'menuBtn')"
has "部署器下載鏈接"                          1   "$(echo "$H" | grep -c 'download/XinSpectDeploy.exe')"
chk "無直連主程式鏈接"                        0   "$(echo "$H" | grep -c 'download/XinSpect.exe')"
chk "無直連終端機鏈接"                        0   "$(echo "$H" | grep -c 'download/XinSpectConsole.exe')"
has "事實域分布節在頁上"                      1   "$(echo "$H" | grep -c 'id="domains"')"
has "誠實邊界節在頁上"                        1   "$(echo "$H" | grep -c 'id="limits"')"
has "安裝步驟與資產對照在頁上"                 1   "$(echo "$H" | grep -c 'asset-table')"
chk "feed.xml 200"                          200 "$(code "$BASE/feed.xml")"
chk "feed.xml 20 items"                     20  "$(curl -s "$BASE/feed.xml" | grep -c '<item>')"
chk "index-en.html 200（跟隨 308 正規化）"    200 "$(curl -sL -o /dev/null -w "%{http_code}" "$BASE/index-en.html")"
has "zh↔en hreflang 互通"                     2   "$(echo "$H" | grep -c 'hreflang="en"')"
has "EN 頁連回 zh"                            1   "$(curl -sL "$BASE/index-en.html" | grep -c 'hreflang="zh-Hant"')"
has "EN 頁 JSON-LD"                           2   "$(curl -sL "$BASE/index-en.html" | grep -c 'application/ld+json')"
chk "og-en.png 200"                         200 "$(code "$BASE/og-en.png")"
for img in shot-xsc-facts shot-xsc-sys shot-xsc-book; do
  chk "截圖 $img.png 200"                    200 "$(code "$BASE/$img.png")"
done
has "錨點 scroll-margin 修復"                   1   "$(echo "$H" | grep -c 'scroll-margin-top')"
has "‘/’篩選快捷鍵（zh）"                      1   "$(echo "$H" | grep -c "e.key==='/'")"
has "行動端語言切換可見（zh）"                  1   "$(echo "$H" | grep -c 'nav a:not(.dl):not(#lang)')"
has "行動端語言切換可見（en）"                  1   "$(curl -sL "$BASE/index-en.html" | grep -c 'nav a:not(.dl){display:none}#lang{display:inline-block}')"
has "GitHub 星標徽章（zh）"                     1   "$(echo "$H" | grep -c 'gh-star')"
has "color-scheme meta（zh）"                  1   "$(echo "$H" | grep -c 'color-scheme')"
has "color-scheme meta（en）"                  1   "$(curl -sL "$BASE/index-en.html" | grep -c 'color-scheme')"
chk "feed.xml language 標籤"                 1   "$(curl -s "$BASE/feed.xml" | grep -c '<language>zh-Hant</language>')"
has "百科線上閱讀連結（zh）"                    1   "$(echo "$H" | grep -c 'docs/%E7%99%BE%E7%A7%91-v3.17.md')"
has "百科線上閱讀連結（en）"                    1   "$(curl -sL "$BASE/index-en.html" | grep -c 'docs/%E7%99%BE%E7%A7%91-v3.17.md')"
has "EN 贊助區塊（含複製按鈕目標）"            1   "$(curl -sL "$BASE/index-en.html" | grep -c 'sponsor-table')"
has "GitHub 星標徽章（en）"                     1   "$(curl -sL "$BASE/index-en.html" | grep -c 'gh-star')"
has "贊助地址複製按鈕"                        1   "$(echo "$H" | grep -c 'copyBtn')"
has "CSP 頭"                                1   "$(curl -s -I "$BASE/" | grep -ci 'content-security-policy')"
_D="obj/_v_changelog.json"; _F="obj/_v_feed.xml"; curl -s "$BASE/changelog.json" > "$_D"; curl -s "$BASE/feed.xml" > "$_F"
chk "feed 首筆日期＝最新版日期"               1   "$(python -I -c "
import json,sys,xml.etree.ElementTree as ET
d=json.load(open(sys.argv[1],encoding='utf-8'))
pd=ET.parse(sys.argv[2]).getroot().find('channel/item/pubDate').text
print(1 if pd.startswith(d[0]['date'] or '1970') else 0)" "$_D" "$_F")"; rm -f "$_D" "$_F"
# 內聯 JS 語法守衛：抓線上頁面的 script 逐一 node --check（防手編破語法）
mkdir -p obj/_v_js
curl -s "$BASE/?v=$RANDOM$RANDOM" > obj/_v_js/zh.html
curl -s "$BASE/index-en.html?v=$RANDOM$RANDOM" > obj/_v_js/en.html
chk "zh HTML 靜態檢查（配對/重複id/img alt）"  0  "$(python -I -X utf8 Tools/check-html.py obj/_v_js/zh.html)"
chk "en HTML 靜態檢查（配對/重複id/img alt）"  0  "$(python -I -X utf8 Tools/check-html.py obj/_v_js/en.html)"
chk "zh 內聯 JS 語法"  0  "$(python -I Tools/check-js.py obj/_v_js/zh.html)"
chk "en 內聯 JS 語法"  0  "$(python -I Tools/check-js.py obj/_v_js/en.html)"
rm -rf obj/_v_js
chk "nosniff 頭"                            1   "$(curl -s -I "$BASE/" | grep -ci 'x-content-type-options: nosniff')"
chk "Referrer-Policy 頭"                    1   "$(curl -s -I "$BASE/" | grep -ci 'referrer-policy')"

# changelog.json 對帳：筆數與最新版號須等於專案版本
J=$(curl -s "$BASE/changelog.json?v=$RANDOM")
CNT=$(grep -c "Version = " "$(dirname "$0")/../Nav/ChangelogCatalog.cs"); chk "changelog.json 筆數等於目錄（$CNT）" "$CNT" "$(echo "$J" | python -I -c "import json,sys;print(len(json.load(sys.stdin)))")"
VER=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$(dirname "$0")/../XinSpect.csproj" | head -1)
chk "最新版號等於專案版本（$VER）"            "$VER" "$(echo "$J" | python -I -c "import json,sys;print(json.load(sys.stdin)[0]['v'])")"

if [ "$fail" = 0 ]; then echo "—— 全部通過"; else echo "—— 有 FAIL，請修"; exit 1; fi
