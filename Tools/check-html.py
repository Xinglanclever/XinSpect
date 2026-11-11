# HTML 靜態檢查：標籤配對、重複 id、img 缺 alt。
# 用法：python -I Tools/check-html.py <page.html>   →  印出問題數（0＝乾淨），問題明細走 stderr
import re, sys
from html.parser import HTMLParser

path = sys.argv[1]
h = open(path, encoding="utf-8").read()

VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr"}

problems = []
stack = []
ids = {}
in_comment = False

class Checker(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
    def handle_starttag(self, tag, attrs):
        if tag in VOID:
            if tag == "img":
                d = dict(attrs)
                if not d.get("alt"):
                    problems.append("img 缺 alt（line %d）" % self.getpos()[0])
            return
        stack.append((tag, self.getpos()[0]))
        for k, v in attrs:
            if k == "id" and v:
                if v in ids:
                    problems.append("重複 id=%s（line %d，首見 line %d）" % (v, self.getpos()[0], ids[v]))
                else:
                    ids[v] = self.getpos()[0]
    def handle_endtag(self, tag):
        if tag in VOID:
            return
        if not stack:
            problems.append("多餘 </%s>（line %d）" % (tag, self.getpos()[0]))
            return
        top, line = stack[-1]
        if top == tag:
            stack.pop()
        else:
            # 容錯：p/li 的瀏覽器自動閉合不計問題，只報真正錯位
            names = [t for t, _ in stack]
            if tag in names:
                while stack and stack[-1][0] != tag:
                    problems.append("未閉合 <%s>（開於 line %d，被 </%s> line %d 跨越）"
                                    % (stack[-1][0], stack[-1][1], tag, self.getpos()[0]))
                    stack.pop()
                stack.pop()
            else:
                problems.append("錯位 </%s>（line %d，當前堆疊頂 %s）" % (tag, self.getpos()[0], top))

# script 內容先抽走，避免 JS 裡的 < > 干擾 parser
h2 = re.sub(r"<script[^>]*>.*?</script>", "<script></script>", h, flags=re.S)
c = Checker()
c.feed(h2)
c.close()
for t, line in stack:
    problems.append("未閉合 <%s>（開於 line %d）" % (t, line))

for p in problems:
    sys.stderr.write(p + "\n")
print(len(problems))
