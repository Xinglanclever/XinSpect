# 從 FactKeyCatalog.cs 計算唯一目錄鍵數。用法：python -I Tools/count-keys.py
import re

h = open("Services/FactKeyCatalog.cs", encoding="utf-8-sig").read()
seg = h[h.index("Keys { get; }"):]
m = re.search(r"=\s*\[(.*?)\];", seg, re.S)
keys = set(re.findall(r'"([a-z][a-z0-9]*(?:\.[a-z0-9_]+)+)"', m.group(1)))
print(len(keys))
