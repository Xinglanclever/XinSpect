# 資產表 vs GitHub Release 實況：用法 python -I Tools/check-assets.py <zh.html> <rel.json>
# 印出大小不符的資產數（0＝一致）
import json, re, sys

h = open(sys.argv[1], encoding="utf-8").read()
rel = json.load(open(sys.argv[2], encoding="utf-8"))
sizes = {m.group(1): m.group(2) for m in
         re.finditer(r"<td>([A-Za-z.-]+\.(?:exe|sys))</td><td>([0-9,]+) bytes</td>", h)}
bad = [a["name"] for a in rel.get("assets", [])
       if a["name"] in sizes and format(a["size"], ",") != sizes[a["name"]]]
sys.stderr.write(str(bad) + "\n")
print(len(bad))
