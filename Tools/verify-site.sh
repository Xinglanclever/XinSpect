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
chk "nosniff 頭"                            1   "$(curl -s -I "$BASE/" | grep -ci 'x-content-type-options: nosniff')"
chk "Referrer-Policy 頭"                    1   "$(curl -s -I "$BASE/" | grep -ci 'referrer-policy')"

# changelog.json 對帳：筆數與最新版號須等於專案版本
J=$(curl -s "$BASE/changelog.json?v=$RANDOM")
CNT=$(grep -c "Version = " "$(dirname "$0")/../Nav/ChangelogCatalog.cs"); chk "changelog.json 筆數等於目錄（$CNT）" "$CNT" "$(echo "$J" | python -I -c "import json,sys;print(len(json.load(sys.stdin)))")"
VER=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$(dirname "$0")/../XinSpect.csproj" | head -1)
chk "最新版號等於專案版本（$VER）"            "$VER" "$(echo "$J" | python -I -c "import json,sys;print(json.load(sys.stdin)[0]['v'])")"

if [ "$fail" = 0 ]; then echo "—— 全部通過"; else echo "—— 有 FAIL，請修"; exit 1; fi
