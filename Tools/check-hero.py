# hero kicker 版本檢查：用法 python -I Tools/check-hero.py <zh.html> <VER>
import re, sys

h = open(sys.argv[1], encoding="utf-8").read()
m = re.search(r"v([0-9]+\.[0-9]+) ULTIMA", h)
print(1 if m and m.group(1) == sys.argv[2] else 0)
