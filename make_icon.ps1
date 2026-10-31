# 產生 XinSpect 的應用程式圖示（Assets/app.ico）。
#
# 這支腳本以前用 System.Drawing 直接畫「曉光儀表」的向量標記。從 3.08 之後，圖示
# 改用使用者提供的品牌插畫（Assets/brand-source.jpg：藍色 X ＋ 兩張視窗卡），
# 實際的裁切、縮放與多尺寸打包都在 Tools/make-icon.py 裡（PIL，每個尺寸都從 1024
# 母圖重取樣，不是拿 256 再放大）。
#
# 這裡只留一個入口，是為了不讓「圖示怎麼來的」這件事散成兩份說明：
# 換品牌圖 → 丟到 Assets/brand-source.jpg → 跑這支 → 提交 app.ico。
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

& python "Tools/make-icon.py" @args
if ($LASTEXITCODE -ne 0) { Write-Error "圖示產生失敗（python Tools/make-icon.py）"; exit 1 }
