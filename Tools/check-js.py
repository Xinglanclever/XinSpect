# 內聯 JS 語法檢查：對指定的 html 檔抽取 <script> 區塊，逐一 node --check。
# 用法：python -I Tools/check-js.py <page.html>   →  印出失敗塊數（0＝全過）
import re, sys, subprocess, os

html = sys.argv[1]
base = os.path.splitext(html)[0]
h = open(html, encoding="utf-8").read()
scripts = re.findall(r"<script>(.*?)</script>", h, re.S)
bad = 0
for i, s in enumerate(scripts):
    f = base + "_" + str(i) + ".js"
    with open(f, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(s)
    r = subprocess.run(["node", "--check", f], capture_output=True)
    if r.returncode != 0:
        bad += 1
        sys.stderr.write(r.stderr.decode("utf-8", "replace")[:400] + "\n")
print(bad)
